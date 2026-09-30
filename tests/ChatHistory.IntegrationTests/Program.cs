using BPSR_ZDPS;
using BPSR_ZDPS.DataTypes;
using BPSR_ZDPS.DataTypes.Chat;
using BPSR_ZDPS.Managers;
using BPSR_ZDPSLib;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using Zproto;
using static Zproto.ChitChatNtf.Types;

internal static partial class Program
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly ChitChatChannelType Team = ChitChatChannelType.ChannelTeam;
    private static readonly ChitChatChannelType World = ChitChatChannelType.ChannelWorld;
    private static int passed;
    private static int failed;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0) return await RunProcessFixture(args);
        await Run("capture, shutdown, reopen restores messages and senders", RestoreMessages);
        await Run("separate writer and reader processes restore only the isolated database without replay", FreshProcessRestoration);
        await Run("separate concurrent installs keep different settings and histories through restart, update and rollback",
            () => SeparateInstallationProfiles(identicalSettings: false));
        await Run("byte-identical settings and copied profile IDs cannot merge concurrent installation histories",
            () => SeparateInstallationProfiles(identicalSettings: true));
        await Run("legacy working-directory settings guard makes no filesystem changes", LegacySettingsGuard);
        await Run("blocked installation data path exposes failure without fallback or settings changes", BlockedInstallationProfile);
        await Run("original protobuf bytes and metadata survive session-ID rewriting", OriginalPayloadAndMetadata);
        await Run("restoration and reload never replay live callbacks or countdown commands", NeverReplayCallbacks);
        await Run("restored messages obey current tab filters, blocked users and stickers", CurrentFilters);
        await Run("tabs added after restoration immediately contain matching history", AddTabAfterRestore);
        await Run("display cap shrink preserves history and cap growth reloads it", DisplayCapDoesNotDelete);
        await Run("sender cache follows visible messages across channel caps and reloads", BoundedSenders);
        await Run("reused game IDs and identical packets remain distinct across sessions", ReusedIdsAndRepeatedText);
        await Run("malformed saved payloads are skipped with a visible error and preserved on disk", MalformedSavedPayloads);
        await Run("live packets and overlapping reloads merge without loss or duplication", ConcurrentLiveReload);
        await Run("rejected live events keep capture order when merged with later committed events", RejectedEventMergeOrder);
        await Run("shutdown rejects further persistence and can be followed by reopen", ShutdownRejectsPersistence);
        await Run("explicit initialization of a different store clears previous display history", SwitchingStores);
        await Run("disabled chat does not capture or notify", DisabledChat);
        Console.WriteLine($"\nChat history integration tests: {passed} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static async Task<int> RunProcessFixture(string[] args)
    {
        if (args[0] is "--profile-fixture" or "--blocked-profile-fixture") return await RunInstallationFixture(args);
        try
        {
            Check(args.Length == 2 && args[0] is "--write-fixture" or "--read-fixture",
                "Fixture usage: --write-fixture <new-database-path> or --read-fixture <existing-database-path>.");
            var databasePath = Path.GetFullPath(args[1]);
            bool writing = args[0] == "--write-fixture";
            Check(writing ? !File.Exists(databasePath) : File.Exists(databasePath),
                writing ? "Writer fixture refuses to overwrite an existing database." : "Reader fixture requires an existing database.");
            Settings.Instance = new Settings();
            Settings.Instance.Chat.MaxChatHistory = 10;
            AppState.IsChatEnabled = true;
            int callbacks = 0;
            void OnMessage(User user, ChatMessage message, NotifyNewestChitChatMsgs packet) => callbacks++;
            ChatManager.OnChatMessage += OnMessage;
            try
            {
                // An explicit path is mandatory: neither process touches the user's normal profile.
                ChatManager.AddChatTab(TabConfig(Team, World));
                ChatManager.InitializeHistory(databasePath);
                await ChatManager.HistoryLoadTask.WaitAsync(Timeout);
                if (writing)
                {
                    Equal(0, ChatManager.Messages.Count);
                    Capture(Packet("/countdown 10", originalId: 1234, senderId: 11, timestamp: 1_700_000_001));
                    Capture(Packet("fresh-process 日本語", originalId: 1234, senderId: 22,
                        channel: World, timestamp: 1_700_000_002));
                    Equal(2, callbacks);
                }
                else
                {
                    Equal(0, callbacks);
                    Equal(2, ChatManager.Senders.Count);
                    Equal("sender-11", ChatManager.Senders[11].Info.Name);
                    Equal("sender-22", ChatManager.Senders[22].Info.Name);
                    await ChatManager.ReloadHistoryAsync();
                    Equal(0, callbacks);
                }
                Equal(2, ChatManager.Messages.Count);
                Sequence(["/countdown 10", "fresh-process 日本語"], TabTexts());
                Check(ChatManager.HistoryRestoreError == null, "Synthetic fixtures must restore without errors.");
                Check(ChatManager.ShutdownHistory(), "Fixture process must drain and close its store cleanly.");
                var rows = ReadRows(databasePath);
                Equal(2, rows.Count);
                Check(rows.All(x => x.OriginalId == 1234), "Original game IDs must survive the process boundary.");
                Console.WriteLine($"{(writing ? "WRITER_OK" : "READER_OK")} pid={Environment.ProcessId} rows=2 callbacks={callbacks}");
                return 0;
            }
            finally { ChatManager.OnChatMessage -= OnMessage; }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PROCESS_FIXTURE_FAIL {ex}");
            return 1;
        }
        finally { ChatManager.ShutdownHistory(); }
    }

    private static async Task FreshProcessRestoration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "zdps-chat-process-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string databasePath = Path.Combine(directory, "isolated-history.db");
        try
        {
            var writer = await RunChild("--write-fixture", databasePath);
            var reader = await RunChild("--read-fixture", databasePath);
            Check(writer.ProcessId != reader.ProcessId, "Restoration must run in a different process from capture.");
            Check(writer.ProcessId != Environment.ProcessId && reader.ProcessId != Environment.ProcessId,
                "The fixture must not use the test runner's existing static state.");
            Check(writer.Output.Contains("WRITER_OK", StringComparison.Ordinal), "Writer did not confirm durable fixture creation.");
            Check(reader.Output.Contains("READER_OK", StringComparison.Ordinal) && reader.Output.Contains("callbacks=0", StringComparison.Ordinal),
                "Reader did not confirm restoration without replay.");
            Console.WriteLine($"  {writer.Output.Trim()}\n  {reader.Output.Trim()}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(int ProcessId, string Output)> RunChild(string mode, string databasePath)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the test executable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Support both `dotnet suite.dll` and the native apphost used by `dotnet run`.
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(databasePath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Fixture process did not start.");
        int processId = process.Id;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(Timeout); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        string stdout = await output, stderr = await errors;
        Check(process.ExitCode == 0, $"{mode} failed with exit code {process.ExitCode}: {stdout}\n{stderr}");
        return (processId, stdout);
    }

    private static async Task Run(string name, Func<Task> test)
    {
        try
        {
            await test().WaitAsync(Timeout);
            passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            failed++;
            Console.Error.WriteLine($"FAIL {name}: {ex}");
        }
        finally
        {
            ChatManager.ShutdownHistory();
        }
    }

    private static async Task RestoreMessages()
    {
        await using var f = await Fixture.Create();
        var first = Packet("hello 日本語", originalId: 731, senderId: 11, channel: Team, timestamp: 1_700_000_001);
        first.VRequest.ChatMsg.SendCharInfo.Name = "Alice";
        var second = Packet("world", originalId: 731, senderId: 12, channel: World, timestamp: 1_700_000_002);
        second.VRequest.ChatMsg.SendCharInfo.Name = "Bob";
        Capture(first);
        Capture(second);
        Equal(2, ChatManager.Messages.Count);
        await f.Reopen();
        Equal(2, ChatManager.Messages.Count);
        Equal(2, ChatManager.Senders.Count);
        Equal("Alice", ChatManager.Senders[11].Info.Name);
        Equal("Bob", ChatManager.Senders[12].Info.Name);
        var restored = ChatManager.Messages.Values.Single(x => x.Msg.MsgText == "hello 日本語");
        Equal(Team, restored.Channel);
        Equal(11L, restored.SenderId);
        Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_001).LocalDateTime, restored.TimeStamp);
        Equal(80, ChatManager.Senders[11].Info.Level);
        await ChatManager.ReloadHistoryAsync();
        Equal(2, ChatManager.Messages.Count);
    }

    private static async Task NeverReplayCallbacks()
    {
        await using var f = await Fixture.Create();
        int callbacks = 0, countdowns = 0;
        void OnMessage(User user, ChatMessage message, NotifyNewestChitChatMsgs packet)
        {
            callbacks++;
            if (message.Msg.MsgText.StartsWith("/countdown", StringComparison.Ordinal)) countdowns++;
        }
        ChatManager.OnChatMessage += OnMessage;
        try
        {
            Capture(Packet("/countdown 10"));
            Capture(Packet("ordinary live text"));
            Equal(2, callbacks);
            Equal(1, countdowns);
            await f.Reopen();
            await ChatManager.ReloadHistoryAsync();
            Equal(2, ChatManager.Messages.Count);
            Equal(2, callbacks);
            Equal(1, countdowns);
            Capture(Packet("/countdown 5"));
            Equal(3, callbacks);
            Equal(2, countdowns);
        }
        finally { ChatManager.OnChatMessage -= OnMessage; }
    }

    private static async Task OriginalPayloadAndMetadata()
    {
        await using var f = await Fixture.Create();
        const long originalId = 812345678901;
        var packet = Packet("original bytes 日本語", originalId: originalId, senderId: 312,
            channel: ChitChatChannelType.ChannelPrivate, level: 64, timestamp: 1_700_000_321);
        packet.VRequest.ChatMsg.MsgInfo.TargetId = 654;
        // Unknown field 101 is valid protobuf and must remain available for later readers.
        var bytes = packet.ToByteArray().Concat(new byte[] { 0xA8, 0x06, 0x7B }).ToArray();
        var receivedAt = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(6789);
        long observedLiveId = originalId;
        void OnMessage(User user, ChatMessage message, NotifyNewestChitChatMsgs envelope) => observedLiveId = envelope.VRequest.ChatMsg.MsgId;
        ChatManager.OnChatMessage += OnMessage;
        try { ChatManager.ProcessChatMessage(bytes, new ExtraPacketData(receivedAt)); }
        finally { ChatManager.OnChatMessage -= OnMessage; }
        Check(observedLiveId != originalId, "The test must exercise the live session-ID rewrite.");
        Check(ChatManager.ShutdownHistory(), "Shutdown must persist the original packet.");
        var row = ReadRows(f.DatabasePath).Single();
        Equal(originalId, row.OriginalId);
        Equal((int)ChitChatChannelType.ChannelPrivate, row.Channel);
        Equal(312L, row.SenderId);
        Equal(654L, row.TargetId);
        Equal(1_700_000_321L, row.SentAt);
        Equal(receivedAt.Ticks, row.ReceivedAtTicks);
        Check(row.EventId != Guid.Empty && row.RunId != Guid.Empty, "Event and capture-run IDs must be nonempty.");
        Sequence(bytes, row.Payload);
        Equal(originalId, NotifyNewestChitChatMsgs.Parser.ParseFrom(row.Payload).VRequest.ChatMsg.MsgId);
        await f.Reopen();
        Equal(654L, ChatManager.Messages.Values.Single().Msg.TargetId);
        Check(ChatManager.ShutdownHistory(), "Readback must not enqueue replacement rows.");
        var afterRestore = ReadRows(f.DatabasePath).Single();
        Equal(row.EventId, afterRestore.EventId);
        Equal(row.RunId, afterRestore.RunId);
        Sequence(row.Payload, afterRestore.Payload);
    }

    private static async Task CurrentFilters()
    {
        await using var f = await Fixture.Create();
        var config = TabConfig(Team);
        config.Contains = "no match while capturing";
        Settings.Instance.WindowSettings.ChatWindow.HideStickers = true;
        ChatManager.AddChatTab(config);
        Capture(Packet("raid keep", senderId: 1));
        Capture(Packet("raid spam", senderId: 2));
        Capture(Packet("raid low", senderId: 3, level: 20));
        Capture(Packet("raid blocked", senderId: 4));
        Capture(Packet("raid wrong channel", senderId: 5, channel: World));
        Capture(Packet("unrelated", senderId: 6));
        var sticker = Packet("", senderId: 7);
        sticker.VRequest.ChatMsg.MsgInfo.MsgType = ChitChatMsgType.ChatMsgPictureEmoji;
        sticker.VRequest.ChatMsg.MsgInfo.PictureEmoji = new ChatPictureEmoji();
        Capture(sticker);
        Equal(0, TabTexts().Length);

        config.Contains = "^raid";
        config.DoesNotContain = "spam$";
        config.OverLevel = 50;
        Settings.Instance.Chat.BlockedUsers[4] = new UserBlock { ID = 4, Name = "sender-4" };
        Settings.Instance.WindowSettings.ChatWindow.HideStickers = true;
        await f.Reopen();
        Equal(7, ChatManager.Messages.Count);
        Sequence(["raid keep"], TabTexts());

        Settings.Instance.WindowSettings.ChatWindow.HideStickers = false;
        ChatManager.RefilterAllTabs();
        Equal(2, TabTexts().Length);
        Check(ChatManager.ChatTabs[0].MessageIds.Any(id => ChatManager.Messages[id].Msg.MsgType == ChitChatMsgType.ChatMsgPictureEmoji),
            "Restored sticker should appear when HideStickers is disabled.");

        ChatManager.UnblockUser(4);
        ChatManager.RefilterAllTabs();
        Check(TabTexts().Contains("raid blocked"), "Unblocked restored sender should appear after refilter.");
        config.Contains = "[";
        ChatManager.RefilterAllTabs();
        Equal(1, TabTexts().Length); // Non-text messages do not use the text regex.
    }

    private static async Task AddTabAfterRestore()
    {
        await using var f = await Fixture.Create();
        Capture(Packet("team first", timestamp: 1_700_000_001));
        Capture(Packet("world", channel: World, timestamp: 1_700_000_002));
        Capture(Packet("team last", timestamp: 1_700_000_003));
        await f.Reopen();
        ChatManager.AddChatTab(TabConfig(Team));
        Sequence(["team first", "team last"], TabTexts());
        ChatManager.AddChatTab(TabConfig(World));
        Sequence(["world"], TabTexts(1));
    }

    private static async Task DisplayCapDoesNotDelete()
    {
        await using var f = await Fixture.Create(cap: 3);
        ChatManager.AddChatTab(TabConfig(Team, World));
        for (int i = 0; i < 8; i++) Capture(Packet($"team-{i}", timestamp: 1_700_000_000 + i));
        for (int i = 0; i < 5; i++) Capture(Packet($"world-{i}", channel: World, timestamp: 1_700_000_100 + i));
        Equal(3, ChatManager.Messages.Values.Count(x => x.Channel == Team));
        Equal(3, ChatManager.Messages.Values.Count(x => x.Channel == World));
        Equal(6, TabTexts().Length);
        Settings.Instance.Chat.MaxChatHistory = 1;
        ChatManager.RemoveMessagesOverCap(1);
        await ChatManager.HistoryLoadTask;
        Sequence(["team-7", "world-4"], TabTexts());
        await f.Reopen();
        Equal(2, ChatManager.Messages.Count);
        Equal(13, ReadRows(f.DatabasePath).Count);
        Settings.Instance.Chat.MaxChatHistory = 100;
        await ChatManager.ReloadHistoryAsync();
        Equal(13, ChatManager.Messages.Count);
        Check(ChatManager.ShutdownHistory(), "Shutdown after cap growth must succeed.");
        Equal(13, ReadRows(f.DatabasePath).Count);
        Equal(13, TabTexts().Length);
        Check(ChatManager.Messages.Values.Any(x => x.Msg.MsgText == "team-0"), "Oldest row must survive display trimming.");
        await ChatManager.ReloadHistoryAsync();
        Equal(13, ChatManager.Messages.Count);
    }

    private static async Task BoundedSenders()
    {
        await using var f = await Fixture.Create(cap: 2);
        Capture(Packet("shared team", senderId: 99));
        Capture(Packet("shared world", senderId: 99, channel: World));
        Capture(Packet("team one", senderId: 1));
        Capture(Packet("team two", senderId: 2));
        Check(ChatManager.Senders.ContainsKey(99), "Sender must survive eviction in one channel while visible in another.");
        Equal(3, ChatManager.Senders.Count);
        Capture(Packet("world three", senderId: 3, channel: World));
        Capture(Packet("world four", senderId: 4, channel: World));
        Check(!ChatManager.Senders.ContainsKey(99), "Sender must be released after its final displayed message is evicted.");
        for (int i = 0; i < 20; i++) Capture(Packet($"bounded-{i}", senderId: 100 + i));
        Equal(4, ChatManager.Messages.Count);
        Equal(4, ChatManager.Senders.Count);
        Settings.Instance.Chat.MaxChatHistory = 1;
        ChatManager.RemoveMessagesOverCap(1);
        await ChatManager.HistoryLoadTask;
        Equal(2, ChatManager.Messages.Count);
        Equal(2, ChatManager.Senders.Count);
        Settings.Instance.Chat.MaxChatHistory = 100;
        await ChatManager.ReloadHistoryAsync();
        Equal(26, ChatManager.Messages.Count);
        Equal(25, ChatManager.Senders.Count);
        Equal("sender-99", ChatManager.Senders[99].Info.Name);
        await f.Reopen();
        Equal(26, ChatManager.Messages.Count);
        Equal(25, ChatManager.Senders.Count);
    }

    private static async Task ReusedIdsAndRepeatedText()
    {
        await using var f = await Fixture.Create();
        var repeated = Packet("identical", originalId: 9, senderId: 1, timestamp: 1_700_000_001);
        Capture(repeated);
        Capture(repeated); // The exact same original ID, sender, text, channel and timestamp.
        Capture(Packet("identical", originalId: 9, senderId: 1, channel: World, timestamp: 1_700_000_001));
        await f.Reopen();
        Equal(3, ChatManager.Messages.Count);
        Capture(repeated); // A new capture session may reuse every game-supplied field.
        await f.Reopen();
        Equal(4, ChatManager.Messages.Count);
        Equal(4, ChatManager.Messages.Keys.Distinct().Count());
        Equal(3, ChatManager.Messages.Values.Count(x => x.Channel == Team));
        for (int i = 0; i < 3; i++) await ChatManager.ReloadHistoryAsync();
        Equal(4, ChatManager.Messages.Count);
        Check(ChatManager.ShutdownHistory(), "All repeated packets must be saved.");
        var rows = ReadRows(f.DatabasePath);
        Equal(4, rows.Count);
        Equal(4, rows.Select(x => x.EventId).Distinct().Count());
        Equal(2, rows.Select(x => x.RunId).Distinct().Count());
        Check(rows.All(x => x.OriginalId == 9), "The original game ID must remain reusable and unchanged.");
        Check(rows.All(x => NotifyNewestChitChatMsgs.Parser.ParseFrom(x.Payload).VRequest.ChatMsg.MsgInfo.MsgText == "identical"),
            "Repeated text must not be content-deduplicated.");
    }

    private static async Task MalformedSavedPayloads()
    {
        await using var f = await Fixture.Create();
        for (int i = 0; i < 6; i++) Capture(Packet($"row-{i}", timestamp: 1_700_000_000 + i));
        Check(ChatManager.ShutdownHistory(), "Baseline rows must be durable before corrupting payloads.");
        var original = ReadRows(f.DatabasePath);
        var missingSender = Packet("missing sender");
        missingSender.VRequest.ChatMsg.SendCharInfo = null;
        var invalidTimestamp = Packet("invalid timestamp", timestamp: long.MaxValue);
        var missingSticker = Packet("missing sticker");
        missingSticker.VRequest.ChatMsg.MsgInfo.MsgType = ChitChatMsgType.ChatMsgPictureEmoji;
        var damagedPayloads = new[] { new byte[] { 0xFF }, missingSender.ToByteArray(), invalidTimestamp.ToByteArray(), missingSticker.ToByteArray() };
        using (var connection = OpenDatabase(f.DatabasePath))
        {
            for (int i = 0; i < damagedPayloads.Length; i++)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE chat_messages SET payload = $payload WHERE event_id = $id";
                command.Parameters.Add("$payload", SqliteType.Blob).Value = damagedPayloads[i];
                command.Parameters.AddWithValue("$id", original[i].EventId.ToString("N"));
                Equal(1, command.ExecuteNonQuery());
            }
        }
        await f.Reopen();
        Equal(2, ChatManager.Messages.Count);
        Check(ChatManager.HistoryRestoreError is { Length: > 0 }, "Skipped malformed payloads must be visible in restore status.");
        Check(ChatManager.HistoryRestoreError!.Contains("4", StringComparison.Ordinal), "Restore status must report all four invalid rows.");
        Check(ChatManager.ShutdownHistory(), "Skipping bad protobuf must not break an otherwise valid store.");
        var afterRestore = ReadRows(f.DatabasePath);
        Equal(6, afterRestore.Count);
        for (int i = 0; i < damagedPayloads.Length; i++) Sequence(damagedPayloads[i], afterRestore[i].Payload);
        for (int i = damagedPayloads.Length; i < original.Count; i++) Sequence(original[i].Payload, afterRestore[i].Payload);
        using var verification = OpenDatabase(f.DatabasePath);
        using var integrity = verification.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check";
        Equal("ok", Convert.ToString(integrity.ExecuteScalar()));
    }

    private static async Task RejectedEventMergeOrder()
    {
        await using var f = await Fixture.Create(cap: 2, maxPendingMessages: 1);
        ChatManager.AddChatTab(TabConfig(Team));
        using (var locker = OpenDatabase(f.DatabasePath))
        using (var transaction = locker.BeginTransaction())
        {
            Capture(Packet("pending-P", timestamp: 1_700_000_001));
            Capture(Packet("rejected-A", timestamp: 1_700_000_002));
            Equal(1L, ChatManager.HistoryStatus!.RejectedCount);
            Equal(1, ChatManager.HistoryStatus.PendingCount);
            Check(ChatManager.HistoryStatus.LastRejection is { Length: > 0 }, "Queue rejection must be visible.");
            transaction.Commit();
        }
        await WaitForWrites();
        Capture(Packet("persisted-B", timestamp: 1_700_000_003));
        await WaitForWrites();
        await ChatManager.ReloadHistoryAsync();
        Sequence(["rejected-A", "persisted-B"], TabTexts());
        Capture(Packet("persisted-C", timestamp: 1_700_000_004));
        Sequence(["persisted-B", "persisted-C"], TabTexts());
        await WaitForWrites();
        Check(!ChatManager.ShutdownHistory(), "A known rejected event must prevent a false all-saved shutdown result.");
        Sequence(["pending-P", "persisted-B", "persisted-C"], ReadRows(f.DatabasePath)
            .Select(x => NotifyNewestChitChatMsgs.Parser.ParseFrom(x.Payload).VRequest.ChatMsg.MsgInfo.MsgText));
        ChatManager.InitializeHistory(f.DatabasePath);
        await ChatManager.HistoryLoadTask;
        Sequence(["persisted-B", "persisted-C"], TabTexts());
        Equal(0L, ChatManager.HistoryStatus!.RejectedCount);
    }

    private static async Task WaitForWrites()
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (ChatManager.HistoryStatus!.PendingCount != 0)
        {
            Check(DateTime.UtcNow < deadline, "Accepted history did not become durable in time.");
            await Task.Delay(10);
        }
    }

    private static async Task ConcurrentLiveReload()
    {
        const int existingCount = 100;
        const int liveCount = 300;
        await using var f = await Fixture.Create(cap: existingCount + liveCount + 1);
        for (int i = 0; i < existingCount; i++) Capture(Packet($"old-{i}", timestamp: 1_700_000_000 + i));
        await f.Reopen();
        ChatManager.AddChatTab(TabConfig(Team));
        int callbacks = 0;
        void OnMessage(User user, ChatMessage message, NotifyNewestChitChatMsgs packet) => Interlocked.Increment(ref callbacks);
        ChatManager.OnChatMessage += OnMessage;
        try
        {
            using var start = new ManualResetEventSlim();
            var producer = Task.Run(async () =>
            {
                start.Wait();
                for (int i = 0; i < liveCount; i++)
                {
                    Capture(Packet($"live-{i}", timestamp: 1_700_001_000 + i));
                    if (i % 10 == 0) await Task.Yield();
                }
            });
            var reloader = Task.Run(async () =>
            {
                start.Wait();
                for (int i = 0; i < 20; i++) await ChatManager.ReloadHistoryAsync();
            });
            start.Set();
            await Task.WhenAll(producer, reloader).WaitAsync(Timeout);
            Equal(existingCount + liveCount, ChatManager.Messages.Count);
            Equal(existingCount + liveCount, ChatManager.Messages.Values.Select(x => x.Msg.MsgText).Distinct().Count());
            Equal(existingCount + liveCount, TabTexts().Length);
            Equal(liveCount, callbacks);
            await f.Reopen();
            Equal(existingCount + liveCount, ChatManager.Messages.Count);
            Equal(existingCount + liveCount, TabTexts().Length);
            Equal(liveCount, callbacks);
        }
        finally { ChatManager.OnChatMessage -= OnMessage; }
    }

    private static async Task ShutdownRejectsPersistence()
    {
        await using var f = await Fixture.Create();
        Capture(Packet("saved before shutdown"));
        Check(ChatManager.ShutdownHistory(), "Shutdown must report a completed drain.");
        var priorIds = ChatManager.Messages.Keys.Order().ToArray();
        Capture(Packet("must not persist after shutdown"));
        Sequence(priorIds, ChatManager.Messages.Keys.Order());
        Check(ChatManager.ShutdownHistory(), "Repeated shutdown must be safe.");
        await f.Reopen();
        Sequence(["saved before shutdown"], ChatManager.Messages.Values.Select(x => x.Msg.MsgText).ToArray());
        Capture(Packet("saved after reopen"));
        await f.Reopen();
        Equal(2, ChatManager.Messages.Count);
    }

    private static async Task SwitchingStores()
    {
        await using var f = await Fixture.Create();
        ChatManager.AddChatTab(TabConfig(Team));
        Capture(Packet("old store", senderId: 123));
        Check(ChatManager.ShutdownHistory(), "Shutdown must drain before switching stores.");
        ChatManager.InitializeHistory(Path.Combine(f.DirectoryPath, "different.db"));
        await ChatManager.HistoryLoadTask.WaitAsync(Timeout);
        Equal(0, ChatManager.Messages.Count);
        Equal(0, ChatManager.Senders.Count);
        Equal(0, TabTexts().Length);
        Capture(Packet("different store", senderId: 456));
        await f.Reopen();
        Sequence(["old store"], ChatManager.Messages.Values.Select(x => x.Msg.MsgText).ToArray());
        Check(!ChatManager.Senders.ContainsKey(456), "A sender from a different database must not leak into this store.");
    }

    private static async Task DisabledChat()
    {
        await using var f = await Fixture.Create();
        int callbacks = 0;
        void OnMessage(User user, ChatMessage message, NotifyNewestChitChatMsgs packet) => callbacks++;
        ChatManager.OnChatMessage += OnMessage;
        try
        {
            AppState.IsChatEnabled = false;
            Capture(Packet("disabled"));
            Equal(0, ChatManager.Messages.Count);
            Equal(0, callbacks);
            AppState.IsChatEnabled = true;
            Capture(Packet("enabled"));
            await f.Reopen();
            Sequence(["enabled"], ChatManager.Messages.Values.Select(x => x.Msg.MsgText).ToArray());
            Equal(1, callbacks);
        }
        finally
        {
            AppState.IsChatEnabled = true;
            ChatManager.OnChatMessage -= OnMessage;
        }
    }

    private static NotifyNewestChitChatMsgs Packet(string text, long originalId = 987654321,
        long senderId = 1, ChitChatChannelType? channel = null, int level = 80, long timestamp = 1_700_000_000) => new()
    {
        VRequest = new NotifyNewestChitChatMsgsRequest
        {
            ChannelType = channel ?? Team,
            ChatMsg = new ChitChatMsg
            {
                MsgId = originalId,
                Timestamp = timestamp,
                SendCharInfo = new BasicShowInfo { CharID = senderId, Name = $"sender-{senderId}", Level = level },
                MsgInfo = new ChatMsgInfo { MsgType = ChitChatMsgType.ChatMsgTextMessage, MsgText = text }
            }
        }
    };

    private static void Capture(NotifyNewestChitChatMsgs packet) =>
        ChatManager.ProcessChatMessage(packet.ToByteArray(), new ExtraPacketData(DateTime.UtcNow));

    private static ChatTabConfig TabConfig(params ChitChatChannelType[] channels) => new()
    {
        Id = Random.Shared.NextInt64(), Name = "integration", OverLevel = 0, Channels = channels.ToList()
    };

    private sealed record SavedRow(Guid EventId, Guid RunId, long OriginalId, int Channel,
        long SenderId, long TargetId, long SentAt, long ReceivedAtTicks, byte[] Payload);

    private static SqliteConnection OpenDatabase(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static List<SavedRow> ReadRows(string path)
    {
        using var connection = OpenDatabase(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, capture_run_id, original_message_id, channel, sender_id,
                   target_id, sent_at_unix_seconds, received_at_utc_ticks, payload
            FROM chat_messages ORDER BY sequence
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<SavedRow>();
        while (reader.Read()) rows.Add(new SavedRow(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
            reader.GetInt64(2), reader.GetInt32(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6),
            reader.GetInt64(7), (byte[])reader[8]));
        return rows;
    }

    private static string[] TabTexts(int tab = 0)
    {
        lock (ChatManager.ChatTabs)
        lock (ChatManager.ChatTabs[tab].MessageIds)
            return ChatManager.ChatTabs[tab].MessageIds.Select(id => ChatManager.Messages[id].Msg.MsgText).ToArray();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException($"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "zdps-chat-integration-" + Guid.NewGuid().ToString("N"));
        public string DatabasePath => Path.Combine(DirectoryPath, "history.db");

        public static async Task<Fixture> Create(int cap = 100, int maxPendingMessages = 10000)
        {
            ChatManager.ShutdownHistory();
            lock (ChatManager.ChatTabs) ChatManager.ChatTabs.Clear();
            ChatManager.Senders.Clear();
            Settings.Instance = new Settings();
            Settings.Instance.Chat.MaxChatHistory = cap;
            AppState.IsChatEnabled = true;
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.DirectoryPath);
            ChatManager.InitializeHistory(fixture.DatabasePath, maxPendingMessages);
            await ChatManager.HistoryLoadTask.WaitAsync(Timeout);
            return fixture;
        }

        public async Task Reopen()
        {
            Check(ChatManager.ShutdownHistory(), "Shutdown must drain all accepted history before reopen.");
            ChatManager.InitializeHistory(DatabasePath);
            await ChatManager.HistoryLoadTask.WaitAsync(Timeout);
        }

        public ValueTask DisposeAsync()
        {
            ChatManager.ShutdownHistory();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
