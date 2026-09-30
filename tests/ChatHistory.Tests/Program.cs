using BPSR_ZDPS.Features.ChatHistory;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Text;

var tests = new (string Name, Func<Task> Run)[]
{
    ("round-trip metadata and exact payload across reopen", RoundTrip),
    ("semicolon database filename is literal and persists on disk", SemicolonDatabasePath),
    ("per-channel display cap never deletes persisted rows", DisplayCap),
    ("identical payloads and reused game IDs stay distinct", DistinctEvents),
    ("stable event retry is idempotent before/after reopen", IdempotentRetry),
    ("concurrent appends and reads never lose accepted events", ConcurrentAppend),
    ("pending overlay is isolated and read success preserves write errors", PendingAndRecovery),
    ("bounded queue rejects explicitly and reports rejected events", BoundedQueue),
    ("old event retry cannot jump to the newest display slot", OldRetryOverlay),
    ("shutdown drains, closes and rejects late writes", Shutdown),
    ("failed shutdown retains explicit unsaved status", FailedShutdown),
    ("flush cancellation does not cancel persistence", FlushCancellation),
    ("corrupt database is preserved byte-for-byte", CorruptDatabase),
    ("structural corruption is detected before journal changes", StructurallyCorruptDatabase),
    ("unknown, future and malformed schema databases are preserved", UnknownDatabases),
    ("a persisted event ID collision never overwrites original bytes", ConflictingPersistedEvent),
    ("failed open recovers when its directory becomes available", OpenRecovery),
    ("unexpected database triggers are preserved but never executed", UnknownTrigger),
    ("invalid input does not enter the queue", InvalidInput)
};
int failures = 0;
foreach (var test in tests)
{
    var timer = Stopwatch.StartNew();
    try { await test.Run(); Console.WriteLine($"PASS {test.Name} ({timer.ElapsedMilliseconds} ms)"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}\n{ex}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} chat history tests passed.");
return failures == 0 ? 0 : 1;

static ChatHistoryEntry Entry(int channel = 1, long original = 42, string text = "synthetic chat fixture") => new(
    Guid.NewGuid(), Guid.NewGuid(), original, channel, 12345, 67890, 1700000000,
    new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(123), Encoding.UTF8.GetBytes(text));

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task Eventually(Func<bool> condition, string message, int milliseconds = 8000)
{
    var timer = Stopwatch.StartNew();
    while (!condition())
    {
        if (timer.ElapsedMilliseconds > milliseconds) throw new TimeoutException(message);
        await Task.Delay(20);
    }
}

static IReadOnlyList<ChatHistoryEntry> Read(ChatHistoryStore store, int cap = 10000)
{
    Assert(store.TryReadRecent(cap, out var entries), "Read failed: " + store.Status.Error);
    return entries;
}

static async Task Commit(ChatHistoryStore store)
{
    Assert(await store.FlushAsync(TimeSpan.FromSeconds(15)), "Flush failed: " + store.Status.Error);
    Assert(store.Status.PendingCount == 0, "Flush left pending entries.");
}

static SqliteConnection Open(string path)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = path, Pooling = false, DefaultTimeout = 1
    }.ToString());
    connection.Open();
    return connection;
}

static void Sql(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static long Count(string path)
{
    using var connection = Open(path);
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM chat_messages";
    return Convert.ToInt64(command.ExecuteScalar());
}

static async Task RoundTrip()
{
    using var directory = new TestDirectory();
    var entry = Entry(-23, long.MaxValue, "合成テスト\0\uFFFD🎮") with
    {
        SenderId = long.MinValue, TargetId = long.MaxValue, SentAtUnixSeconds = -123456,
        Payload = [0, 255, 8, 128, 1, 26, 0, 99]
    };
    var expected = entry.Payload.ToArray();
    using (var store = new ChatHistoryStore(directory.Database))
    {
        Assert(store.Append(entry), "Append rejected.");
        entry.Payload[0] = 100;
        await Commit(store);
    }
    using var reopened = new ChatHistoryStore(directory.Database);
    var actual = Read(reopened).Single();
    Assert(actual.EventId == entry.EventId && actual.CaptureRunId == entry.CaptureRunId &&
        actual.OriginalMessageId == entry.OriginalMessageId && actual.Channel == entry.Channel &&
        actual.SenderId == entry.SenderId && actual.TargetId == entry.TargetId &&
        actual.SentAtUnixSeconds == entry.SentAtUnixSeconds && actual.ReceivedAtUtc == entry.ReceivedAtUtc &&
        actual.ReceivedAtUtc.Kind == DateTimeKind.Utc && actual.Sequence > 0 &&
        actual.Payload.SequenceEqual(expected), "Round-trip metadata or exact bytes changed.");
    actual.Payload[0] = 12;
    Assert(Read(reopened).Single().Payload.SequenceEqual(expected), "Returned payload mutation changed stored data.");
    Assert(ChatHistoryStore.DefaultDatabasePath == Path.Combine(Path.GetFullPath(AppContext.BaseDirectory), "Data", "ChatHistory", "history.sqlite3"), "Default path must belong to this installation, not a shared user profile.");
}

static async Task SemicolonDatabasePath()
{
    using var directory = new TestDirectory();
    // This is a database filename, not an executable location. Connection-string-like text
    // must remain a literal filename rather than changing SQLite mode or cache settings.
    string path = Path.Combine(directory.Path, "chat;Mode=Memory;Cache=Shared.sqlite3");
    var entry = Entry(text: "semicolon filename survives a fresh connection");
    using (var store = new ChatHistoryStore(path))
    {
        Assert(store.DatabasePath == Path.GetFullPath(path), "The database path was altered.");
        Assert(store.Append(entry), "Append rejected.");
        await Commit(store);
        Assert(File.Exists(path), "The database must be created at the exact semicolon filename.");
    }
    using (var reopened = new ChatHistoryStore(path))
    {
        var restored = Read(reopened).Single();
        Assert(restored.EventId == entry.EventId && restored.Payload.SequenceEqual(entry.Payload),
            "Semicolons were interpreted as connection options or the stored event was lost.");
    }
    Assert(Count(path) == 1, "The exact on-disk database must retain its row after reopening.");
    Assert(!File.Exists(Path.Combine(directory.Path, "chat")), "A truncated database filename was created.");
}

static async Task DisplayCap()
{
    using var directory = new TestDirectory();
    using var store = new ChatHistoryStore(directory.Database);
    var entries = Enumerable.Range(0, 60).Select(i => Entry(i % 3, i)).ToArray();
    foreach (var entry in entries) Assert(store.Append(entry), "Append rejected.");
    await Commit(store);
    foreach (int cap in new[] { 5, 1, 12, 2, 100 })
    {
        var actual = Read(store, cap);
        Assert(actual.Count == 3 * Math.Min(20, cap), "Incorrect per-channel cap.");
        foreach (var channel in entries.GroupBy(x => x.Channel))
            Assert(actual.Where(x => x.Channel == channel.Key).Select(x => x.EventId)
                .SequenceEqual(channel.TakeLast(cap).Select(x => x.EventId)), "Wrong recent events for channel.");
        Assert(Count(directory.Database) == entries.Length, "Display cap deleted archive rows.");
    }
}

static async Task DistinctEvents()
{
    using var directory = new TestDirectory();
    using var store = new ChatHistoryStore(directory.Database);
    var first = Entry();
    var second = first with { EventId = Guid.NewGuid() };
    var otherCapture = first with { EventId = Guid.NewGuid(), CaptureRunId = Guid.NewGuid() };
    foreach (var entry in new[] { first, second, otherCapture }) Assert(store.Append(entry), "Append rejected.");
    await Commit(store);
    Assert(Read(store).Count == 3 && Count(directory.Database) == 3, "Identical/reused original IDs were deduplicated.");
}

static async Task IdempotentRetry()
{
    using var directory = new TestDirectory();
    var entry = Entry();
    using (var store = new ChatHistoryStore(directory.Database))
    {
        Assert(store.Append(entry), "Append rejected.");
        Assert(store.Append(entry), "Pending/committed retry rejected.");
        await Commit(store);
        Assert(store.Append(entry), "Committed retry rejected.");
        await Commit(store);
    }
    using var reopened = new ChatHistoryStore(directory.Database);
    Assert(reopened.Append(entry), "Retry after reopen rejected.");
    await Commit(reopened);
    Assert(Count(directory.Database) == 1, "A retry created another persisted event.");
}

static async Task ConcurrentAppend()
{
    using var directory = new TestDirectory();
    using var store = new ChatHistoryStore(directory.Database, 2000);
    var entries = Enumerable.Range(0, 480).Select(i => Entry(i % 4, i % 7)).ToArray();
    using var reading = new CancellationTokenSource();
    var reader = Task.Run(async () =>
    {
        while (!reading.IsCancellationRequested)
        {
            var actual = Read(store, 9);
            Assert(actual.Select(x => x.EventId).Distinct().Count() == actual.Count, "A read returned duplicate events.");
            Assert(actual.GroupBy(x => x.Channel).All(x => x.Count() <= 9), "Concurrent read exceeded display cap.");
            await Task.Delay(2);
        }
    });
    try
    {
        await Task.WhenAll(entries.Chunk(60).Select(batch => Task.Run(() =>
        {
            foreach (var entry in batch) Assert(store.Append(entry), "Concurrent append rejected.");
        })));
        await Commit(store);
    }
    finally { reading.Cancel(); await reader; }
    Assert(Count(directory.Database) == entries.Length, "Concurrent accepted events were lost.");
    Assert(Read(store).Select(x => x.EventId).ToHashSet().SetEquals(entries.Select(x => x.EventId)), "Wrong concurrent event IDs.");
}

static async Task PendingAndRecovery()
{
    using var directory = new TestDirectory();
    using var store = new ChatHistoryStore(directory.Database);
    Read(store);
    using var blocker = Open(directory.Database);
    Sql(blocker, "BEGIN IMMEDIATE");
    var entry = Entry();
    var expected = entry.Payload.ToArray();
    try
    {
        Assert(store.Append(entry), "Append rejected.");
        await Eventually(() => store.Status.WriteError != null, "No write failure status while locked.");
        var pending = Read(store).Single();
        Assert(pending.EventId == entry.EventId && pending.Sequence == 0, "Missing pending overlay.");
        Assert(store.Status.WriteError != null && store.Status.HasFailure, "Successful read cleared write failure.");
        pending.Payload[0] ^= 0xFF;
        entry.Payload[0] ^= 0xEE;
        Assert(Read(store).Single().Payload.SequenceEqual(expected), "Pending overlay shared mutable bytes.");
        Assert(!await store.FlushAsync(TimeSpan.FromMilliseconds(30)), "Flush reported durability while writer was locked.");
    }
    finally { Sql(blocker, "ROLLBACK"); }
    await Commit(store);
    Assert(store.Status.WriteError == null && !store.Status.HasFailure, "Write failure did not recover after unlock.");
    Assert(Read(store).Single().Payload.SequenceEqual(expected), "Recovery changed captured payload.");
}

static async Task BoundedQueue()
{
    using var directory = new TestDirectory();
    using var store = new ChatHistoryStore(directory.Database, 2);
    Read(store);
    using var blocker = Open(directory.Database);
    Sql(blocker, "BEGIN IMMEDIATE");
    var first = Entry();
    try
    {
        Assert(store.Append(first) && store.Append(Entry(2)), "Queue rejected before capacity.");
        Assert(store.TryAppend(first) == ChatHistoryAppendResult.AlreadyPending, "Same event retry consumed capacity.");
        Assert(store.TryAppend(first with { Payload = [9] }) == ChatHistoryAppendResult.ConflictingEvent, "Conflicting pending ID was accepted.");
        Assert(store.TryAppend(Entry()) == ChatHistoryAppendResult.QueueFull, "Overflow was accepted.");
        Assert(store.Status.PendingCount == 2 && store.Status.RejectedCount == 2 && store.Status.LastRejection != null,
            "Queue/rejection status is inaccurate.");
    }
    finally { Sql(blocker, "ROLLBACK"); }
    await Eventually(() => store.Status.PendingCount == 0, "Accepted queue entries failed to drain.");
    Assert(Count(directory.Database) == 2, "Overflow or rejected conflict was persisted.");
    Assert(!store.Flush(TimeSpan.FromSeconds(2)) && store.Status.HasFailure, "Rejected event history was silently cleared.");
}

static async Task OldRetryOverlay()
{
    using var directory = new TestDirectory();
    using var store = new ChatHistoryStore(directory.Database);
    var old = Entry();
    var latest = Entry();
    Assert(store.Append(old) && store.Append(latest), "Append rejected.");
    await Commit(store);
    using var blocker = Open(directory.Database);
    Sql(blocker, "BEGIN IMMEDIATE");
    try
    {
        Assert(store.Append(old), "Old retry rejected.");
        Assert(Read(store, 1).Single().EventId == latest.EventId, "Pending retry moved an old persisted event to the newest display slot.");
    }
    finally { Sql(blocker, "ROLLBACK"); }
    await Commit(store);
}

static async Task Shutdown()
{
    using var directory = new TestDirectory();
    var store = new ChatHistoryStore(directory.Database);
    for (int i = 0; i < 100; i++) Assert(store.Append(Entry()), "Append rejected.");
    var first = store.ShutdownAsync(TimeSpan.FromSeconds(10));
    var second = store.ShutdownAsync(TimeSpan.Zero);
    Assert(ReferenceEquals(first, second), "Concurrent shutdowns did not share a task.");
    Assert(await first, "Healthy shutdown reported failure.");
    Assert(store.Status.IsStopped && !store.Status.IsAccepting && store.Status.PendingCount == 0, "Shutdown status incorrect.");
    Assert(Count(directory.Database) == 100, "Shutdown failed to drain accepted events.");
    Assert(store.TryAppend(Entry()) == ChatHistoryAppendResult.Stopped, "Write after shutdown accepted.");
    Assert(!store.TryReadRecent(10, out _), "Read after shutdown reopened the store.");
    store.Dispose();
    await store.DisposeAsync();
}

static async Task FailedShutdown()
{
    using var directory = new TestDirectory();
    var store = new ChatHistoryStore(directory.Database);
    Read(store);
    using var blocker = Open(directory.Database);
    Sql(blocker, "BEGIN IMMEDIATE");
    try
    {
        Assert(store.Append(Entry()), "Append rejected.");
        Assert(!await store.ShutdownAsync(TimeSpan.FromMilliseconds(30)), "Locked shutdown claimed durability.");
        Assert(store.Status.IsStopped && store.Status.PendingCount == 1 &&
            store.Status.WriteError?.Contains("not saved") == true, "Shutdown silently discarded pending state.");
    }
    finally { Sql(blocker, "ROLLBACK"); store.Dispose(); }
    Assert(Count(directory.Database) == 0, "Timed-out event unexpectedly appeared.");
}

static async Task FlushCancellation()
{
    using var directory = new TestDirectory();
    using var store = new ChatHistoryStore(directory.Database);
    Read(store);
    using var blocker = Open(directory.Database);
    Sql(blocker, "BEGIN IMMEDIATE");
    try
    {
        Assert(store.Append(Entry()), "Append rejected.");
        using var cancellation = new CancellationTokenSource(50);
        bool cancelled = false;
        try { await store.FlushAsync(Timeout.InfiniteTimeSpan, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Assert(cancelled && store.Status.IsAccepting && store.Status.PendingCount == 1, "Flush cancellation stopped persistence.");
    }
    finally { Sql(blocker, "ROLLBACK"); }
    await Commit(store);
}

static async Task CorruptDatabase()
{
    using var directory = new TestDirectory();
    byte[] original = Encoding.UTF8.GetBytes("synthetic invalid sqlite fixture\0\uFFFF");
    File.WriteAllBytes(directory.Database, original);
    await VerifyRejectedDatabase(directory.Database, original);
}

static async Task StructurallyCorruptDatabase()
{
    using var directory = new TestDirectory();
    using (var store = new ChatHistoryStore(directory.Database))
    {
        Assert(store.Append(Entry()), "Append rejected.");
        await Commit(store);
    }
    long pageOffset;
    using (var connection = Open(directory.Database))
    {
        Sql(connection, "PRAGMA journal_mode=DELETE");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rootpage FROM sqlite_master WHERE name='chat_messages'";
        long page = Convert.ToInt64(command.ExecuteScalar());
        command.CommandText = "PRAGMA page_size";
        pageOffset = (page - 1) * Convert.ToInt64(command.ExecuteScalar());
    }
    var bytes = File.ReadAllBytes(directory.Database);
    bytes[pageOffset] = 0; // Invalid b-tree page type; header and schema page remain valid.
    File.WriteAllBytes(directory.Database, bytes);
    await VerifyRejectedDatabase(directory.Database, bytes);
}

static async Task UnknownDatabases()
{
    foreach (string sql in new[]
    {
        "CREATE TABLE unrelated (id INTEGER PRIMARY KEY, value TEXT); INSERT INTO unrelated VALUES (1,'fixture');",
        "PRAGMA user_version=99; PRAGMA application_id=1514350676; CREATE TABLE future (id INTEGER);",
        "CREATE VIEW unrelated_view AS SELECT 1;",
        "PRAGMA user_version=1; PRAGMA application_id=1514358868; CREATE TABLE chat_messages (sequence TEXT);",
        "PRAGMA user_version=1; PRAGMA application_id=1514358868; CREATE TABLE chat_messages (sequence INTEGER PRIMARY KEY, event_id TEXT NOT NULL, capture_run_id TEXT NOT NULL, original_message_id INTEGER NOT NULL, channel INTEGER NOT NULL, sender_id INTEGER NOT NULL, target_id INTEGER NOT NULL, sent_at_unix_seconds INTEGER NOT NULL, received_at_utc_ticks INTEGER NOT NULL, payload BLOB NOT NULL);"
    })
    {
        using var directory = new TestDirectory();
        using (var connection = Open(directory.Database)) Sql(connection, sql);
        await VerifyRejectedDatabase(directory.Database, File.ReadAllBytes(directory.Database));
    }
}

static async Task VerifyRejectedDatabase(string path, byte[] original)
{
    var store = new ChatHistoryStore(path);
    Assert(!store.TryReadRecent(5, out var empty) && empty.Count == 0, "Unknown/corrupt database was read as valid.");
    var pending = Entry();
    Assert(store.Append(pending), "Queue rejected fixture unexpectedly.");
    await Eventually(() => store.Status.WriteError != null, "Unknown/corrupt DB did not report write error.");
    Assert(!store.TryReadRecent(5, out var overlay) && overlay.Single().EventId == pending.EventId,
        "Failed database read lost pending overlay.");
    Assert(!await store.ShutdownAsync(TimeSpan.FromMilliseconds(10)), "Unknown/corrupt DB shutdown claimed success.");
    store.Dispose();
    Assert(File.ReadAllBytes(path).SequenceEqual(original), "Unknown/corrupt database bytes were changed or replaced.");
}

static async Task UnknownTrigger()
{
    using var directory = new TestDirectory();
    using (var store = new ChatHistoryStore(directory.Database))
    {
        Assert(store.Append(Entry()), "Append rejected.");
        await Commit(store);
    }
    using (var connection = Open(directory.Database))
        Sql(connection, "CREATE TRIGGER retention AFTER INSERT ON chat_messages BEGIN DELETE FROM chat_messages WHERE sequence <> new.sequence; END;");
    await VerifyRejectedDatabase(directory.Database, File.ReadAllBytes(directory.Database));
    Assert(Count(directory.Database) == 1, "Unknown retention trigger executed.");
}

static async Task ConflictingPersistedEvent()
{
    using var directory = new TestDirectory();
    var store = new ChatHistoryStore(directory.Database);
    var original = Entry();
    Assert(store.Append(original), "Append rejected.");
    await Commit(store);
    Assert(store.Append(original with { Payload = [9, 9, 9] }), "New queue submission rejected unexpectedly.");
    await Eventually(() => store.Status.WriteError != null, "Conflicting persisted ID was silently ignored.");
    Assert(!await store.FlushAsync(TimeSpan.FromMilliseconds(10)), "Conflicting event claimed durable write.");
    Assert(Read(store).Single().Payload.SequenceEqual(original.Payload), "Conflicting event overwrote original data.");
    Assert(!await store.ShutdownAsync(TimeSpan.FromMilliseconds(10)), "Conflict shutdown claimed success.");
    store.Dispose();
    Assert(Count(directory.Database) == 1, "Conflict changed persisted row count.");
}

static async Task OpenRecovery()
{
    using var directory = new TestDirectory();
    string blockedDirectory = Path.Combine(directory.Path, "blocked");
    File.WriteAllText(blockedDirectory, "synthetic path blocker");
    string database = Path.Combine(blockedDirectory, "history.sqlite3");
    using var store = new ChatHistoryStore(database);
    Assert(store.Append(Entry()), "Append rejected.");
    await Eventually(() => store.Status.WriteError != null, "Directory failure was not reported.");
    Assert(!store.TryReadRecent(5, out var overlay) && overlay.Count == 1, "Open failure lost pending overlay.");
    File.Delete(blockedDirectory);
    Directory.CreateDirectory(blockedDirectory);
    await Commit(store);
    Assert(Read(store).Count == 1 && store.Status.Error == null, "Open failure failed to recover.");
}

static Task InvalidInput()
{
    using var directory = new TestDirectory();
    using var store = new ChatHistoryStore(directory.Database);
    foreach (var entry in new[] { Entry() with { EventId = Guid.Empty }, Entry() with { ReceivedAtUtc = DateTime.Now } })
    {
        bool rejected = false;
        try { store.Append(entry); } catch (ArgumentException) { rejected = true; }
        Assert(rejected, "Invalid entry accepted.");
    }
    bool invalidCap = false;
    try { store.TryReadRecent(0, out _); } catch (ArgumentOutOfRangeException) { invalidCap = true; }
    Assert(invalidCap && store.Status.PendingCount == 0, "Invalid input changed queue.");
    return Task.CompletedTask;
}

sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zdps-chat-tests-" + Guid.NewGuid().ToString("N"));
    public string Database => System.IO.Path.Combine(Path, "history.sqlite3");
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
