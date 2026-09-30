using System.Diagnostics;
using System.Security.Cryptography;
using BPSR_ZDPS;
using BPSR_ZDPS.DataTypes;
using BPSR_ZDPS.Managers;
using BPSR_ZDPSLib;
using Google.Protobuf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Zproto;
using static Zproto.ChitChatNtf.Types;

internal static class Program
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private const string Marker = ".synthetic-installation-settings-test";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 0) return await Fixture(args);
            await SeparateRealSettings();
            Console.WriteLine("Installation settings tests: 1 passed; 0 failed (real app assembly, no stubs, no UI/capture).");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task SeparateRealSettings()
    {
        // The runner never calls Settings.Load/Save in its own build/installation directory.
        string root = Path.Combine(Path.GetTempPath(), "zdps-real-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string a = Path.Combine(root, "copy A; 日本語");
            string b = Path.Combine(root, "copy B");
            string cwd = Path.Combine(root, "shared working directory");
            string home = Path.Combine(root, "shared user context");
            Directory.CreateDirectory(cwd);
            Directory.CreateDirectory(home);
            CopyRuntime(a);
            CopyRuntime(b);
            Seed(a, "A");
            Seed(b, "B");

            // Both real Settings.Load calls and history initialization finish before either save.
            // No startup CWD normalization is used, so installation anchoring is tested directly.
            string gate = Path.Combine(root, "save-gate");
            using (var first = Start(a, cwd, home, root, "A", "write", gate))
            using (var second = Start(b, cwd, home, root, "B", "write", gate))
            {
                try
                {
                    try
                    {
                        await WaitUntil(() => File.Exists(gate + ".A.ready") && File.Exists(gate + ".B.ready"),
                            "Both independent installations must load before concurrent writes.", first.Process, second.Process);
                    }
                    catch
                    {
                        // Surface a child initialization failure, including any native dependency boundary.
                        if (first.Process.HasExited) await first.Complete();
                        if (second.Process.HasExited) await second.Complete();
                        throw;
                    }
                    Check(!first.Process.HasExited && !second.Process.HasExited,
                        "Both installation processes must be alive at the save barrier.");
                    File.WriteAllText(gate, "synthetic fixture release");
                    await Task.WhenAll(first.Complete(), second.Complete());
                    Check(first.Process.Id != second.Process.Id, "The installations must be different processes.");
                }
                finally { first.Stop(); second.Stop(); }
            }
            Console.WriteLine("PASS concurrent real Settings.Load/Save retains separate languages, caps, tab IDs, filters and selection");

            var aAfterSave = Snapshot(Path.Combine(a, "Data"));
            var bAfterSave = Snapshot(Path.Combine(b, "Data"));
            using (var first = Start(a, cwd, home, root, "A", "read", "-"))
            using (var second = Start(b, cwd, home, root, "B", "read", "-"))
            {
                try { await Task.WhenAll(first.Complete(), second.Complete()); }
                finally { first.Stop(); second.Stop(); }
            }
            SameSnapshot(aAfterSave, Snapshot(Path.Combine(a, "Data")), "A restart changed persisted files");
            SameSnapshot(bAfterSave, Snapshot(Path.Combine(b, "Data")), "B restart changed persisted files");
            Console.WriteLine("PASS fresh simultaneous restarts restore only each installation's messages and original tab configuration");

            using (var first = Start(a, cwd, home, root, "A", "edit", "-"))
            {
                try { await first.Complete(); }
                finally { first.Stop(); }
            }
            SameSnapshot(bAfterSave, Snapshot(Path.Combine(b, "Data")), "Saving A touched B's settings or archive");
            Check(!aAfterSave["Settings.json"].SequenceEqual(Snapshot(Path.Combine(a, "Data"))["Settings.json"]),
                "A-only save must actually update A's settings.");
            using (var first = Start(a, cwd, home, root, "A", "read-edited", "-"))
            {
                try { await first.Complete(); }
                finally { first.Stop(); }
            }
            Check(!Directory.EnumerateFileSystemEntries(cwd).Any(), "Shared CWD must remain empty.");
            Check(!Directory.EnumerateFiles(home, "Settings.json", SearchOption.AllDirectories).Any(),
                "No settings may fall back to the shared user context.");
            Check(!Directory.EnumerateFiles(home, "history.sqlite3*", SearchOption.AllDirectories).Any(),
                "No chat archive may fall back to the shared user context.");
            Console.WriteLine("PASS A-only real save leaves every B Data file unchanged and creates no shared-profile data");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<int> Fixture(string[] args)
    {
        Check(args.Length == 5 && args[0] == "--fixture", "Invalid fixture arguments.");
        string root = Path.GetFullPath(args[1]);
        string actor = args[2];
        string mode = args[3];
        Check(actor is "A" or "B", "Unknown fixture installation.");
        Check(mode is "write" or "read" or "edit" or "read-edited", "Unknown fixture mode.");
        // Refuse to operate in the repository, the normal application install, or another caller's root.
        Check(Path.GetFileName(root).StartsWith("zdps-real-settings-", StringComparison.Ordinal) &&
            Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
            "Fixture root must be a dedicated temporary test directory.");
        string expectedInstall = Path.Combine(root, actor == "A" ? "copy A; 日本語" : "copy B");
        Check(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) == expectedInstall &&
            File.ReadAllText(Path.Combine(expectedInstall, Marker)) == actor, "Unsafe fixture location.");
        Check(Environment.CurrentDirectory == Path.Combine(root, "shared working directory"), "Unexpected CWD.");
        string expectedSettings = Path.Combine(expectedInstall, "Data", "Settings.json");
        string expectedDatabase = Path.Combine(expectedInstall, "Data", "ChatHistory", "history.sqlite3");
        Equal(expectedSettings, AppPaths.SettingsFilePath, "Settings path");
        Equal(Path.GetDirectoryName(expectedSettings), BPSR_ZDPS.Utils.DATA_DIR_NAME, "Data path");
        AppPaths.ValidateLegacySettingsLocation();

        // These are the production application's actual full settings model and methods.
        Check(typeof(Settings).Assembly == typeof(ChatManager).Assembly, "Settings must come from the real app assembly.");
        Check(typeof(Settings).Assembly != typeof(Program).Assembly, "Test Settings stubs are forbidden.");
        Settings.Load();
        int revision = mode == "write" ? 0 : mode == "read-edited" ? 2 : 1;
        AssertSettings(actor, revision);
        var before = SerializedSettings(Settings.Instance);
        foreach (var tab in Settings.Instance.WindowSettings.ChatWindow.ChatTabs)
            ChatManager.AddChatTab(tab);
        AppState.IsChatEnabled = true;
        int callbacks = 0;
        void OnMessage(BPSR_ZDPS.DataTypes.Chat.User user, BPSR_ZDPS.DataTypes.Chat.ChatMessage message,
            NotifyNewestChitChatMsgs packet) => callbacks++;
        ChatManager.OnChatMessage += OnMessage;
        try
        {
            // Deliberately omit a path; this proves the app's real default history root.
            ChatManager.InitializeHistory();
            await ChatManager.HistoryLoadTask.WaitAsync(Timeout);
            Equal(expectedDatabase, ChatManager.HistoryStatus!.DatabasePath, "History path");
            Check(ChatManager.HistoryRestoreError == null, "History restore error: " + ChatManager.HistoryRestoreError);
            Equal(0, callbacks, "Restore must not replay live callbacks");
            if (mode == "write")
            {
                Equal(0, ChatManager.Messages.Count, "New fixture history");
                File.WriteAllText(args[4] + "." + actor + ".ready", Environment.ProcessId.ToString());
                await WaitUntil(() => File.Exists(args[4]), "Concurrent save gate was not released.");
                Capture(actor, "team", ChitChatChannelType.ChannelTeam);
                Capture(actor, "world", ChitChatChannelType.ChannelWorld);
                Capture(actor, "excluded", ChitChatChannelType.ChannelTeam);
                Equal(3, callbacks, "Live capture callbacks");
                // Repeated overlapping saves exercise the actual implementation with distinct values.
                for (int i = 0; i < 8; i++)
                {
                    Settings.Instance.FixedFramerateScale = Scale(actor, 1);
                    Settings.Save();
                    await Task.Delay(10);
                }
                revision = 1;
            }
            else if (mode == "edit")
            {
                Settings.Instance.FixedFramerateScale = Scale(actor, 2);
                Settings.Save();
                revision = 2;
            }
            AssertMessages(actor);
            AssertSettings(actor, revision);
            var after = SerializedSettings(Settings.Instance);
            before["FixedFramerateScale"] = Scale(actor, revision);
            Check(JToken.DeepEquals(before, after), "Chat initialization/capture unexpectedly changed other settings.");
            Check(ChatManager.ShutdownHistory(), "History shutdown must drain and close the archive.");
            Check(!ChatManager.HistoryStatus!.HasFailure, "History status must be healthy.");
            if (mode is "write" or "edit")
            {
                Check(JToken.DeepEquals(after, JObject.Parse(File.ReadAllText(expectedSettings))),
                    "Actual Settings.Save must preserve the complete serialized settings model.");
                // Reload the saved file through the real implementation, rather than just inspect JSON.
                Settings.Load();
                AssertSettings(actor, revision);
                Check(JToken.DeepEquals(after, SerializedSettings(Settings.Instance)), "Actual Load/Save round trip changed settings.");
            }
            Console.WriteLine($"FIXTURE_OK actor={actor} mode={mode} pid={Environment.ProcessId} callbacks={callbacks}");
            return 0;
        }
        finally
        {
            ChatManager.OnChatMessage -= OnMessage;
            ChatManager.ShutdownHistory();
        }
    }

    // Normalize JSON numeric representations (CLR float versus parsed JSON double).
    private static JObject SerializedSettings(Settings settings) => JObject.Parse(JsonConvert.SerializeObject(settings));

    private static uint Scale(string actor, int revision) => (uint)((actor == "A" ? 2 : 5) + revision);

    private static JObject SeedSettings(string actor) => new()
    {
        ["Language"] = actor == "A" ? "ja" : "zh",
        ["NetCaptureDeviceName"] = "synthetic-device-" + actor,
        ["OnlyShowPartyMembersInMeters"] = actor == "A",
        ["DatabaseRetentionPolicyDays"] = actor == "A" ? 11 : 23,
        ["FixedFramerateScale"] = Scale(actor, 0),
        ["Chat"] = new JObject
        {
            ["MaxChatHistory"] = actor == "A" ? 17 : 29,
            ["BlockedUsers"] = new JObject
            {
                [actor == "A" ? "501" : "502"] = new JObject
                {
                    ["ID"] = actor == "A" ? 501 : 502,
                    ["Name"] = "synthetic-blocked-" + actor,
                    ["BlockedAt"] = "2025-02-03T04:05:06Z"
                }
            }
        },
        ["WindowSettings"] = new JObject
        {
            ["MainWindow"] = new JObject { ["Opacity"] = actor == "A" ? 81 : 92, ["MeterBarScale"] = actor == "A" ? 1.25 : 1.5 },
            ["ChatWindow"] = new JObject
            {
                ["WindowPosition"] = new JObject { ["X"] = actor == "A" ? 123 : 321, ["Y"] = 456 },
                ["WindowSize"] = new JObject { ["X"] = 777, ["Y"] = actor == "A" ? 555 : 666 },
                ["Opacity"] = actor == "A" ? 73 : 87,
                ["TopMost"] = actor == "B",
                ["BackgroundOpacity"] = actor == "A" ? 0.25 : 0.75,
                ["LastSelectedTabId"] = actor == "A" ? 10102 : 20202,
                ["CompactMode"] = actor == "A",
                ["ShowTime"] = actor == "B",
                ["ShowTimeAsXAgo"] = actor == "A",
                ["HideStickers"] = actor == "B",
                ["SupportClickthrough"] = actor == "A",
                ["ChatTabs"] = new JArray
                {
                    Tab(actor, 1, ChitChatChannelType.ChannelTeam),
                    Tab(actor, 2, ChitChatChannelType.ChannelWorld)
                }
            }
        }
    };

    private static JObject Tab(string actor, int offset, ChitChatChannelType channel) => new()
    {
        ["Name"] = actor + (offset == 1 ? " 自分のチーム" : " 自分のワールド"),
        ["Id"] = (actor == "A" ? 10100 : 20200) + offset,
        ["Channels"] = new JArray((int)channel),
        ["OverLevel"] = actor == "A" ? 55 : 60,
        ["Contains"] = "^" + actor + "-",
        ["DoesNotContain"] = "excluded"
    };

    private static void Seed(string install, string actor)
    {
        Directory.CreateDirectory(Path.Combine(install, "Data"));
        File.WriteAllText(Path.Combine(install, Marker), actor);
        File.WriteAllText(Path.Combine(install, "Data", "Settings.json"), SeedSettings(actor).ToString());
    }

    private static void AssertSettings(string actor, int revision)
    {
        // Derive expected defaults via the real full model, preserving every supplied setting.
        var expected = SeedSettings(actor).ToObject<Settings>()!;
        expected.FixedFramerateScale = Scale(actor, revision);
        Check(JToken.DeepEquals(SerializedSettings(expected), SerializedSettings(Settings.Instance)),
            $"{actor} settings differ from the original configuration (revision {revision}).");
    }

    private static void Capture(string actor, string suffix, ChitChatChannelType channel)
    {
        var packet = new NotifyNewestChitChatMsgs
        {
            VRequest = new NotifyNewestChitChatMsgsRequest
            {
                ChannelType = channel,
                ChatMsg = new ChitChatMsg
                {
                    MsgId = 42,
                    Timestamp = 1_700_000_000,
                    SendCharInfo = new BasicShowInfo { CharID = actor == "A" ? 101 : 202, Name = "synthetic-" + actor, Level = 80 },
                    MsgInfo = new ChatMsgInfo { MsgType = ChitChatMsgType.ChatMsgTextMessage, MsgText = actor + "-" + suffix }
                }
            }
        };
        ChatManager.ProcessChatMessage(packet.ToByteArray(), new ExtraPacketData(DateTime.UtcNow));
    }

    private static void AssertMessages(string actor)
    {
        Equal(3, ChatManager.Messages.Count, "Restored message count");
        var messages = ChatManager.Messages.Values.Select(x => x.Msg.MsgText).Order().ToArray();
        Check(messages.SequenceEqual(new[] { actor + "-excluded", actor + "-team", actor + "-world" }), "Cross-installation message leakage.");
        Equal(1, ChatManager.Senders.Count, "Isolated sender cache");
        Equal(2, ChatManager.ChatTabs.Count, "Saved tab count");
        for (int i = 0; i < 2; i++)
        {
            var tab = ChatManager.ChatTabs[i];
            Equal(1, tab.MessageIds.Count, "Current persisted filters must be applied");
            Equal(actor + (i == 0 ? "-team" : "-world"), ChatManager.Messages[tab.MessageIds[0]].Msg.MsgText, "Filtered tab text");
            Check(JToken.DeepEquals(JObject.FromObject(tab.Config), JObject.FromObject(Settings.Instance.WindowSettings.ChatWindow.ChatTabs[i])),
                "Manager changed saved tab configuration.");
        }
    }

    private static void CopyRuntime(string destination)
    {
        Directory.CreateDirectory(destination);
        // Never copy an existing Settings.json or history: only executors and runtime dependencies.
        foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            string extension = Path.GetExtension(file);
            if (extension is ".dll" or ".json" or ".so" or ".dylib")
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        string runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");
        if (Directory.Exists(runtimes))
            foreach (string file in Directory.EnumerateFiles(runtimes, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, Path.GetRelativePath(AppContext.BaseDirectory, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
    }

    private static Child Start(string install, string cwd, string home, string root, string actor, string mode, string gate)
    {
        string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrEmpty(host) && Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
            host = Environment.ProcessPath;
        var info = new ProcessStartInfo(host ?? "dotnet")
        {
            WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { Path.Combine(install, typeof(Program).Assembly.GetName().Name + ".dll"), "--fixture", root, actor, mode, gate })
            info.ArgumentList.Add(argument);
        foreach (string variable in new[] { "HOME", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "XDG_DATA_HOME", "DOTNET_CLI_HOME" })
            info.Environment[variable] = home;
        return new Child(Process.Start(info) ?? throw new InvalidOperationException("Failed to start fixture."));
    }

    private sealed class Child(Process process) : IDisposable
    {
        public Process Process { get; } = process;
        private readonly Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        private readonly Task<string> stderr = process.StandardError.ReadToEndAsync();
        public async Task Complete()
        {
            await Process.WaitForExitAsync().WaitAsync(Timeout);
            string output = await stdout;
            string errors = await stderr;
            Check(Process.ExitCode == 0 && output.Contains("FIXTURE_OK", StringComparison.Ordinal),
                $"Fixture failed ({Process.ExitCode}):\n{output}\n{errors}");
            Console.Write(output);
        }
        public void Stop() { if (!Process.HasExited) { Process.Kill(entireProcessTree: true); Process.WaitForExit(); } }
        public void Dispose() { Stop(); Process.Dispose(); }
    }

    private static async Task WaitUntil(Func<bool> condition, string failure, params Process[] children)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Check(watch.Elapsed < Timeout && !children.Any(p => p.HasExited), failure);
            await Task.Delay(10);
        }
    }

    private static Dictionary<string, byte[]> Snapshot(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(file => Path.GetRelativePath(root, file), file => SHA256.HashData(File.ReadAllBytes(file)));

    private static void SameSnapshot(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after, string message) =>
        Check(before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var hash) && pair.Value.SequenceEqual(hash)), message);

    private static void Equal<T>(T expected, T actual, string message) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{message}: expected {expected}; got {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
