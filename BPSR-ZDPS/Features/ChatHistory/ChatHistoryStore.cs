using Microsoft.Data.Sqlite;
using System.Threading.Channels;

namespace BPSR_ZDPS.Features.ChatHistory;

/// <summary>One captured event. Generate EventId once, and preserve it on retries.</summary>
public sealed record ChatHistoryEntry(Guid EventId, Guid CaptureRunId, long OriginalMessageId,
    int Channel, long SenderId, long TargetId, long SentAtUnixSeconds, DateTime ReceivedAtUtc,
    byte[] Payload, long Sequence = 0);

public sealed record ChatHistoryStatus(string DatabasePath, int PendingCount, long RejectedCount,
    string? Error)
{
    public string? WriteError { get; init; }
    public string? ReadError { get; init; }
    public string? LastRejection { get; init; }
    public bool IsAccepting { get; init; }
    public bool IsStopped { get; init; }
    public bool HasFailure => Error != null || RejectedCount != 0;
}

public enum ChatHistoryAppendResult { Accepted, AlreadyPending, QueueFull, Stopped, ConflictingEvent }

/// <summary>
/// A dedicated append-only archive, independent of display and encounter retention.
/// Append only copies/enqueues; all writes run on a worker. Accepted entries are volatile until
/// Flush succeeds. A failed shutdown reports unsaved pending entries; it never claims durability.
/// </summary>
public sealed class ChatHistoryStore : IDisposable, IAsyncDisposable
{
    const int SchemaVersion = 1;
    const int ApplicationId = 0x5A434854; // ZCHT
    static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);
    readonly object databaseGate = new();
    readonly object stateGate = new();
    readonly Channel<Pending> queue;
    readonly Dictionary<Guid, Pending> pending = new();
    readonly CancellationTokenSource stop = new();
    readonly Task writer;
    readonly int maxPending;
    TaskCompletionSource progress = NewProgress();
    Task<bool>? shutdownTask;
    SqliteConnection? connection;
    string? writeError, readError, lastRejection;
    long nextOrder, completedOrder, rejected;
    int openGeneration;
    bool accepting = true, stopped;

    sealed record Pending(ChatHistoryEntry Entry, long Order);
    static TaskCompletionSource NewProgress() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static string DefaultDatabasePath => AppPaths.ChatHistoryDatabasePath;
    public string DatabasePath { get; }
    public int OpenGeneration => Volatile.Read(ref openGeneration);
    // Status never waits for SQLite or the writer, so rendering can poll it cheaply.
    public ChatHistoryStatus Status
    {
        get
        {
            lock (stateGate)
                return new(DatabasePath, pending.Count, rejected, writeError ?? readError)
                {
                    WriteError = writeError, ReadError = readError, LastRejection = lastRejection,
                    IsAccepting = accepting, IsStopped = stopped
                };
        }
    }

    public ChatHistoryStore(string databasePath, int maxPendingMessages = 10000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPendingMessages, 1);
        DatabasePath = Path.GetFullPath(databasePath);
        maxPending = maxPendingMessages;
        queue = System.Threading.Channels.Channel.CreateBounded<Pending>(new BoundedChannelOptions(maxPending)
        {
            SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        writer = Task.Run(WriteLoopAsync);
    }

    public bool Append(ChatHistoryEntry entry) => TryAppend(entry) is
        ChatHistoryAppendResult.Accepted or ChatHistoryAppendResult.AlreadyPending;

    public ChatHistoryAppendResult TryAppend(ChatHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(entry.Payload);
        if (entry.EventId == Guid.Empty) throw new ArgumentException("An event must have a stable, nonempty ID.", nameof(entry));
        // An unspecified timestamp has no reliable timezone. The capture layer supplies UTC.
        if (entry.ReceivedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("ReceivedAtUtc must have DateTimeKind.Utc.", nameof(entry));
        lock (stateGate)
        {
            if (!accepting) return Reject(ChatHistoryAppendResult.Stopped, "Chat history has stopped accepting events.");
            if (pending.TryGetValue(entry.EventId, out var existing))
                return SameEvent(existing.Entry, entry) ? ChatHistoryAppendResult.AlreadyPending :
                    Reject(ChatHistoryAppendResult.ConflictingEvent, "The event ID was reused for different chat data.");
            if (pending.Count >= maxPending)
                return Reject(ChatHistoryAppendResult.QueueFull, "The chat history queue is full; this event was not accepted.");
            // Own the bytes; neither caller mutation nor a returned read may mutate queued data.
            var item = new Pending(entry with { Payload = entry.Payload.ToArray(), Sequence = 0 }, nextOrder + 1);
            pending.Add(entry.EventId, item);
            if (!queue.Writer.TryWrite(item))
            {
                pending.Remove(entry.EventId);
                return Reject(ChatHistoryAppendResult.Stopped, "The chat history writer is unavailable.");
            }
            nextOrder = item.Order;
            return ChatHistoryAppendResult.Accepted;
        }
    }

    ChatHistoryAppendResult Reject(ChatHistoryAppendResult result, string message)
    {
        rejected++;
        lastRejection = message;
        return result;
    }

    /// <summary>Waits for accepted events through this call, not later appends. Rejections are sticky.</summary>
    public bool Flush(TimeSpan timeout) => FlushAsync(timeout).GetAwaiter().GetResult();

    public async Task<bool> FlushAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        long target;
        lock (stateGate) target = nextOrder;
        return await WaitForCommitAsync(target, timeout, cancellationToken).ConfigureAwait(false);
    }

    async Task<bool> WaitForCommitAsync(long target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task changed;
                lock (stateGate)
                {
                    if (completedOrder >= target) return rejected == 0;
                    if (stopped) return false;
                    changed = progress.Task;
                }
                await changed.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    /// <summary>
    /// Returns the latest cap per channel, oldest first. The cap never deletes archive rows.
    /// On a database failure, returns false and still provides a snapshot of pending entries.
    /// This method performs disk I/O; call it away from packet/render callbacks.
    /// </summary>
    public bool TryReadRecent(int perChannel, out IReadOnlyList<ChatHistoryEntry> entries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perChannel, 1);
        lock (databaseGate)
        {
            try
            {
                lock (stateGate)
                    if (stopped) throw new ObjectDisposedException(nameof(ChatHistoryStore));
                EnsureOpen();
                // All reads share a SQLite snapshot, including checks for retries of older events.
                using var transaction = connection!.BeginTransaction(deferred: true);
                var rows = new List<ChatHistoryEntry>();
                var channels = new List<int>();
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = "SELECT DISTINCT channel FROM chat_messages";
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) channels.Add(reader.GetInt32(0));
                }
                foreach (int channel in channels)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = $"SELECT {Columns} FROM chat_messages WHERE channel = $channel ORDER BY sequence DESC LIMIT $limit";
                    command.Parameters.AddWithValue("$channel", channel);
                    command.Parameters.AddWithValue("$limit", perChannel);
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) rows.Add(ReadEntry(reader));
                }
                Pending[] overlay;
                lock (stateGate) overlay = pending.Values.OrderBy(x => x.Order).ToArray();
                var persisted = FindPersistedIds(overlay, transaction);
                entries = Limit(rows.OrderBy(x => x.Sequence).Concat(overlay
                    .Where(x => !persisted.Contains(x.Entry.EventId)).Select(x => Clone(x.Entry))), perChannel);
                transaction.Commit();
                lock (stateGate) readError = null; // Never clears a failed write.
                return true;
            }
            catch (Exception ex)
            {
                lock (stateGate)
                {
                    readError = ex.Message;
                    entries = Limit(pending.Values.OrderBy(x => x.Order).Select(x => Clone(x.Entry)), perChannel);
                }
                return false;
            }
        }
    }

    HashSet<Guid> FindPersistedIds(Pending[] overlay, SqliteTransaction transaction)
    {
        var result = new HashSet<Guid>();
        // Batching keeps below SQLite's variable limit, including older builds of SQLite.
        foreach (var batch in overlay.Chunk(500))
        {
            using var command = connection!.CreateCommand();
            command.Transaction = transaction;
            var parameters = batch.Select((x, i) => "$id" + i).ToArray();
            command.CommandText = $"SELECT event_id FROM chat_messages WHERE event_id IN ({string.Join(',', parameters)})";
            for (int i = 0; i < batch.Length; i++)
                command.Parameters.AddWithValue(parameters[i], batch[i].Entry.EventId.ToString("N"));
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(Guid.Parse(reader.GetString(0)));
        }
        return result;
    }

    static IReadOnlyList<ChatHistoryEntry> Limit(IEnumerable<ChatHistoryEntry> source, int cap)
    {
        var rows = source.ToArray();
        var selected = rows.GroupBy(x => x.Channel).SelectMany(x => x.TakeLast(cap))
            .Select(x => x.EventId).ToHashSet();
        return rows.Where(x => selected.Contains(x.EventId)).ToArray();
    }

    async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                while (true)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    try
                    {
                        lock (databaseGate)
                        {
                            EnsureOpen();
                            Insert(item.Entry);
                            // The commit and pending removal share the read lock. No gap or duplicate
                            // can appear between a persisted snapshot and its pending overlay.
                            lock (stateGate)
                            {
                                pending.Remove(item.Entry.EventId);
                                completedOrder = item.Order;
                                writeError = null;
                                SignalProgress();
                            }
                        }
                        break;
                    }
                    catch (Exception ex)
                    {
                        lock (stateGate) writeError = ex.Message;
                        // A failed event stays first in the bounded queue. Never skip it or silently
                        // delete/recreate a database, including after SQLITE_CORRUPT/NOTADB.
                        await Task.Delay(RetryDelay, stop.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { lock (stateGate) writeError = ex.Message; }
        finally
        {
            lock (databaseGate)
            {
                try { connection?.Dispose(); }
                catch (Exception ex) { lock (stateGate) writeError = "Could not close chat history: " + ex.Message; }
                finally
                {
                    connection = null;
                    lock (stateGate)
                    {
                        accepting = false;
                        stopped = true;
                        queue.Writer.TryComplete();
                        if (pending.Count != 0)
                            writeError = $"{pending.Count} accepted chat event(s) were not saved before shutdown. " + writeError;
                        SignalProgress();
                    }
                }
            }
        }
    }

    // Caller holds stateGate. Waiters never run inline while this lock is held.
    void SignalProgress()
    {
        var previous = progress;
        progress = NewProgress();
        previous.TrySetResult();
    }

    void EnsureOpen()
    {
        if (connection != null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var candidate = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 1
        }.ToString());
        try
        {
            candidate.Open();
            using var command = candidate.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            int version = Convert.ToInt32(command.ExecuteScalar());
            command.CommandText = "PRAGMA application_id";
            int applicationId = Convert.ToInt32(command.ExecuteScalar());
            if (version == 0 && applicationId == 0)
            {
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
                if (Convert.ToInt32(command.ExecuteScalar()) != 0)
                    throw new InvalidDataException("This is not an empty chat history database. The file has not been replaced.");
                using var transaction = candidate.BeginTransaction();
                command.Transaction = transaction;
                command.CommandText = $"""
                    CREATE TABLE chat_messages (
                        sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                        event_id TEXT NOT NULL UNIQUE,
                        capture_run_id TEXT NOT NULL,
                        original_message_id INTEGER NOT NULL,
                        channel INTEGER NOT NULL,
                        sender_id INTEGER NOT NULL,
                        target_id INTEGER NOT NULL,
                        sent_at_unix_seconds INTEGER NOT NULL,
                        received_at_utc_ticks INTEGER NOT NULL,
                        payload BLOB NOT NULL
                    );
                    CREATE INDEX chat_messages_channel_sequence ON chat_messages(channel, sequence DESC);
                    PRAGMA application_id = {ApplicationId};
                    PRAGMA user_version = {SchemaVersion};
                    """;
                command.ExecuteNonQuery();
                transaction.Commit();
                command.Transaction = null;
            }
            else if (version != SchemaVersion || applicationId != ApplicationId)
                throw new InvalidDataException($"Unsupported chat history database (schema {version}). The file has not been replaced.");

            ValidateSchema(candidate);
            command.CommandText = "PRAGMA quick_check";
            if (!string.Equals(Convert.ToString(command.ExecuteScalar()), "ok", StringComparison.Ordinal))
                throw new InvalidDataException("The chat history database failed its integrity check. The file has not been replaced.");
            command.CommandText = "PRAGMA journal_mode=WAL";
            if (!string.Equals(Convert.ToString(command.ExecuteScalar()), "wal", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Chat history requires SQLite WAL journaling.");
            command.CommandText = "PRAGMA synchronous=FULL";
            command.ExecuteNonQuery();
            connection = candidate;
            Interlocked.Increment(ref openGeneration);
        }
        catch { candidate.Dispose(); throw; }
    }

    static void ValidateSchema(SqliteConnection candidate)
    {
        using var command = candidate.CreateCommand();
        // Unknown tables/triggers/views could impose retention or change insert behavior. Never
        // write to a database merely because someone reused our application/version markers.
        command.CommandText = """
            SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'
              AND NOT (type = 'table' AND name = 'chat_messages')
              AND NOT (type = 'index' AND name = 'chat_messages_channel_sequence')
            """;
        if (Convert.ToInt32(command.ExecuteScalar()) != 0)
            throw new InvalidDataException("The chat history database contains unknown schema objects. The file has not been replaced.");
        command.CommandText = "PRAGMA table_info(chat_messages)";
        var expected = new[] { "sequence", "event_id", "capture_run_id", "original_message_id", "channel",
            "sender_id", "target_id", "sent_at_unix_seconds", "received_at_utc_ticks", "payload" };
        int count = 0;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (count >= expected.Length || reader.GetString(1) != expected[count] ||
                    reader.GetString(2).ToUpperInvariant() != (count is 1 or 2 ? "TEXT" : count == 9 ? "BLOB" : "INTEGER") ||
                    reader.GetInt32(5) != (count == 0 ? 1 : 0) || (count != 0 && reader.GetInt32(3) != 1))
                    throw new InvalidDataException("The chat history schema is incompatible. The file has not been replaced.");
                count++;
            }
        }
        if (count != expected.Length)
            throw new InvalidDataException("The chat history schema is incomplete. The file has not been replaced.");
        // ON CONFLICT(event_id) must refer to a nonpartial unique index on that exact column.
        command.CommandText = """
            SELECT COUNT(*) FROM pragma_index_list('chat_messages') AS indexes
            WHERE indexes.[unique] = 1 AND indexes.partial = 0
              AND (SELECT COUNT(*) FROM pragma_index_info(indexes.name)) = 1
              AND (SELECT name FROM pragma_index_info(indexes.name)) = 'event_id'
            """;
        if (Convert.ToInt32(command.ExecuteScalar()) == 0)
            throw new InvalidDataException("The chat history event index is missing. The file has not been replaced.");
        command.CommandText = "SELECT name FROM pragma_index_info('chat_messages_channel_sequence') ORDER BY seqno";
        var channelIndex = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) channelIndex.Add(reader.GetString(0));
        if (!channelIndex.SequenceEqual(new[] { "channel", "sequence" }))
            throw new InvalidDataException("The chat history channel index is missing or incompatible. The file has not been replaced.");
    }

    const string Columns = "event_id, capture_run_id, original_message_id, channel, sender_id, target_id, sent_at_unix_seconds, received_at_utc_ticks, payload, sequence";

    static ChatHistoryEntry ReadEntry(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt64(2),
        reader.GetInt32(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
        new DateTime(reader.GetInt64(7), DateTimeKind.Utc), (byte[])reader[8], reader.GetInt64(9));

    void Insert(ChatHistoryEntry entry)
    {
        using var command = connection!.CreateCommand();
        command.CommandText = """
            INSERT INTO chat_messages(event_id, capture_run_id, original_message_id, channel,
                sender_id, target_id, sent_at_unix_seconds, received_at_utc_ticks, payload)
            VALUES($event, $run, $original, $channel, $sender, $target, $sent, $received, $payload)
            ON CONFLICT(event_id) DO NOTHING
            """;
        command.Parameters.AddWithValue("$event", entry.EventId.ToString("N"));
        command.Parameters.AddWithValue("$run", entry.CaptureRunId.ToString("N"));
        command.Parameters.AddWithValue("$original", entry.OriginalMessageId);
        command.Parameters.AddWithValue("$channel", entry.Channel);
        command.Parameters.AddWithValue("$sender", entry.SenderId);
        command.Parameters.AddWithValue("$target", entry.TargetId);
        command.Parameters.AddWithValue("$sent", entry.SentAtUnixSeconds);
        command.Parameters.AddWithValue("$received", entry.ReceivedAtUtc.Ticks);
        command.Parameters.Add("$payload", SqliteType.Blob).Value = entry.Payload;
        if (command.ExecuteNonQuery() != 0) return;
        command.CommandText = $"SELECT {Columns} FROM chat_messages WHERE event_id = $event";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || !SameEvent(ReadEntry(reader), entry))
            throw new InvalidDataException("An existing event ID has different chat data; the original event was preserved.");
    }

    static ChatHistoryEntry Clone(ChatHistoryEntry entry) => entry with { Payload = entry.Payload.ToArray() };
    static bool SameEvent(ChatHistoryEntry first, ChatHistoryEntry second) =>
        first.EventId == second.EventId && first.CaptureRunId == second.CaptureRunId &&
        first.OriginalMessageId == second.OriginalMessageId && first.Channel == second.Channel &&
        first.SenderId == second.SenderId && first.TargetId == second.TargetId &&
        first.SentAtUnixSeconds == second.SentAtUnixSeconds && first.ReceivedAtUtc == second.ReceivedAtUtc &&
        first.Payload.AsSpan().SequenceEqual(second.Payload);

    static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout != Timeout.InfiniteTimeSpan && (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    /// <summary>
    /// Stops accepting, drains accepted entries, and closes SQLite. On timeout the remaining
    /// entries are reported as unsaved. Repeated/concurrent calls share the first shutdown.
    /// </summary>
    public Task<bool> ShutdownAsync(TimeSpan timeout)
    {
        ValidateTimeout(timeout);
        lock (stateGate)
        {
            if (shutdownTask != null) return shutdownTask;
            accepting = false;
            queue.Writer.TryComplete();
            // Start outside the calling lock context: no stateGate -> databaseGate inversion.
            return shutdownTask = Task.Run(() => ShutdownCoreAsync(timeout));
        }
    }

    async Task<bool> ShutdownCoreAsync(TimeSpan timeout)
    {
        long target;
        lock (stateGate) target = nextOrder;
        bool committed = await WaitForCommitAsync(target, timeout, CancellationToken.None).ConfigureAwait(false);
        if (!committed) stop.Cancel();
        await writer.ConfigureAwait(false);
        stop.Dispose();
        lock (stateGate) return completedOrder >= target && pending.Count == 0 && rejected == 0 && writeError == null;
    }

    public void Dispose() => ShutdownAsync(DefaultShutdownTimeout).GetAwaiter().GetResult();
    public async ValueTask DisposeAsync() => await ShutdownAsync(DefaultShutdownTimeout).ConfigureAwait(false);
}
