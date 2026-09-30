using BPSR_ZDPS;
using BPSR_ZDPS.DataTypes;
using BPSR_ZDPS.DataTypes.Chat;
using BPSR_ZDPS.Managers;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zproto;
using static Zproto.ChitChatNtf.Types;

internal static partial class Program
{
    private const string ProfileGuardFile = ".zdps-integration-profile";
    private const string ReleaseFile = ".zdps-integration-release";

    private sealed class ProfileReply
    {
        public string Event { get; set; } = "";
        public int Pid { get; set; } = Environment.ProcessId;
        public string Installation { get; set; } = "";
        public string Database { get; set; } = "";
        public string Settings { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public string Home { get; set; } = "";
        public string LocalAppData { get; set; } = "";
        public string SettingsHash { get; set; } = "";
        public string Release { get; set; } = "";
        public string[] Texts { get; set; } = [];
        public int Callbacks { get; set; }
        public long StartedAt { get; set; }
        public long EndedAt { get; set; }
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string NormalizeDirectory(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static async Task<int> RunInstallationFixture(string[] args)
    {
        try
        {
            // Never initialize the production default unless this executor was copied into a
            // disposable, explicitly marked test installation. No database path is injected.
            Check(args.Length == 3 && Guid.TryParseExact(args[2], "N", out _), "Invalid profile fixture arguments.");
            string installation = NormalizeDirectory(AppContext.BaseDirectory);
            string temporaryRoot = NormalizeDirectory(Path.GetTempPath()) + Path.DirectorySeparatorChar;
            Check(installation.StartsWith(temporaryRoot, StringComparison.Ordinal), "Profile fixture must live under the temporary directory.");
            Check(File.ReadAllText(Path.Combine(installation, ProfileGuardFile)) == args[2], "Missing or mismatched test installation guard.");
            Equal(installation, NormalizeDirectory(AppPaths.InstallationDirectory));
            Equal(Path.Combine(installation, "Data"), NormalizeDirectory(AppPaths.DataDirectory));
            Equal(Path.Combine(installation, "Data", "Settings.json"), AppPaths.SettingsFilePath);
            Equal(Path.Combine(installation, "Data", "ChatHistory", "history.sqlite3"), AppPaths.ChatHistoryDatabasePath);
            AppPaths.ValidateLegacySettingsLocation();
            string settingsHash = HashFile(AppPaths.SettingsFilePath);
            string marker = args[1];
            int callbacks = 0;
            Settings.Instance = new Settings();
            Settings.Instance.Chat.MaxChatHistory = 10000;
            AppState.IsChatEnabled = true;
            void OnMessage(User user, ChatMessage message, NotifyNewestChitChatMsgs packet) => callbacks++;
            ChatManager.OnChatMessage += OnMessage;
            try
            {
                ChatManager.AddChatTab(TabConfig(Team, World));
                ChatManager.InitializeHistory(); // The production default is the subject of this fixture.
                await ChatManager.HistoryLoadTask.WaitAsync(Timeout);
                Equal(AppPaths.ChatHistoryDatabasePath, ChatManager.HistoryStatus!.DatabasePath);
                Equal(0, callbacks);
                if (args[0] == "--blocked-profile-fixture")
                {
                    Check(ChatManager.HistoryRestoreError != null, "Blocked default path must expose a restore error.");
                    Check(ChatManager.HistoryStatus.HasFailure, "Blocked default path must expose storage failure.");
                    Capture(Packet("blocked|pending|0000"));
                    var timer = Stopwatch.StartNew();
                    while (ChatManager.HistoryStatus.WriteError == null)
                    {
                        Check(timer.Elapsed < Timeout, "Blocked writer must report its failure.");
                        await Task.Delay(5);
                    }
                    Equal(1, ChatManager.Messages.Count);
                    Equal(1, callbacks);
                    Equal(1, ChatManager.HistoryStatus.PendingCount);
                    Check(!ChatManager.ShutdownHistory(), "Unsaved blocked writes must not report successful shutdown.");
                    Check(ChatManager.HistoryStatus.HasFailure, "A failed shutdown must retain visible failure.");
                    Equal(settingsHash, HashFile(AppPaths.SettingsFilePath));
                    Equal("blocker preserved", File.ReadAllText(Path.Combine(AppPaths.DataDirectory, "ChatHistory")));
                    Check(!File.Exists(AppPaths.ChatHistoryDatabasePath), "A blocked archive cannot exist at its intended path.");
                    Emit(new ProfileReply { Event = "BLOCKED_OK", Database = ChatManager.HistoryStatus.DatabasePath,
                        SettingsHash = settingsHash, Callbacks = callbacks });
                    return 0;
                }
                await Reply("READY");
                while (await Console.In.ReadLineAsync() is { } line)
                {
                    string[] command = line.Split(' ');
                    if (command[0] is "capture" or "capture-held")
                    {
                        Check(command.Length == 4, "capture requires phase, count, delay.");
                        string phase = command[1];
                        int count = int.Parse(command[2]), delay = int.Parse(command[3]);
                        Check(count is > 0 and <= 1000 && delay is >= 0 and <= 100, "Invalid capture dimensions.");
                        long startedAt = DateTime.UtcNow.Ticks;
                        Emit(new ProfileReply { Event = "CAPTURE_STARTED", StartedAt = startedAt });
                        // A held capture keeps writing until the parent has completed the
                        // other process's restart. No startup-speed assumption is required.
                        Task<string?>? release = command[0] == "capture-held" ? Task.Run(Console.In.ReadLine) : null;
                        var captureTimer = Stopwatch.StartNew();
                        for (int i = 0; i < count || (release != null && !release.IsCompleted); i++)
                        {
                            Check(captureTimer.Elapsed < Timeout && i < 9000, "Held capture did not receive its release command.");
                            Capture(Packet($"{marker}|{phase}|{i:D4}", originalId: 314159,
                                senderId: marker == "install-a" ? 101 : 202));
                            if (delay != 0) await Task.Delay(delay);
                        }
                        long endedAt = DateTime.UtcNow.Ticks;
                        if (release != null) Equal("finish-capture", await release.WaitAsync(Timeout));
                        await Reply("CAPTURE_DONE", startedAt, endedAt);
                    }
                    else if (command[0] == "snapshot") await Reply("SNAPSHOT");
                    else if (command[0] == "stop")
                    {
                        Check(ChatManager.ShutdownHistory(), "Profile shutdown must drain accepted history.");
                        Equal(settingsHash, HashFile(AppPaths.SettingsFilePath));
                        Emit(new ProfileReply { Event = "STOPPED" });
                        return 0;
                    }
                    else throw new InvalidOperationException($"Unknown fixture command: {line}");
                }
                throw new InvalidOperationException("Profile fixture input ended before clean shutdown.");

                async Task Reply(string eventName, long startedAt = 0, long endedAt = 0)
                {
                    var timer = Stopwatch.StartNew();
                    while (ChatManager.HistoryStatus!.PendingCount != 0)
                    {
                        Check(timer.Elapsed < Timeout, "Profile writes did not become durable.");
                        Check(!ChatManager.HistoryStatus.HasFailure, "Profile writes reported a storage error.");
                        await Task.Delay(5);
                    }
                    int beforeReload = callbacks;
                    await ChatManager.ReloadHistoryAsync().WaitAsync(Timeout);
                    Equal(beforeReload, callbacks);
                    Check(ChatManager.HistoryRestoreError == null, "Profile restore failed: " + ChatManager.HistoryRestoreError);
                    Check(!ChatManager.HistoryStatus!.HasFailure, "Profile store reported an error.");
                    string[] texts = TabTexts();
                    Check(texts.All(text => text.StartsWith(marker + "|", StringComparison.Ordinal)), "Another installation's message leaked into this profile.");
                    Equal(settingsHash, HashFile(AppPaths.SettingsFilePath));
                    Emit(new ProfileReply
                    {
                        Event = eventName, Installation = AppPaths.InstallationDirectory,
                        Database = ChatManager.HistoryStatus.DatabasePath, Settings = AppPaths.SettingsFilePath,
                        WorkingDirectory = Environment.CurrentDirectory,
                        Home = Environment.GetEnvironmentVariable("HOME") ?? "",
                        LocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "",
                        SettingsHash = settingsHash, Release = File.ReadAllText(Path.Combine(installation, ReleaseFile)),
                        Texts = texts, Callbacks = callbacks, StartedAt = startedAt,
                        EndedAt = endedAt == 0 ? DateTime.UtcNow.Ticks : endedAt
                    });
                }
            }
            finally { ChatManager.OnChatMessage -= OnMessage; }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"PROFILE_FIXTURE_FAIL {exception}");
            return 1;
        }
        finally { ChatManager.ShutdownHistory(); }
    }

    private static void Emit(ProfileReply reply)
    {
        Console.WriteLine(JsonSerializer.Serialize(reply));
        Console.Out.Flush();
    }

    private static async Task SeparateInstallationProfiles(bool identicalSettings)
    {
        string directory = Path.Combine(Path.GetTempPath(), "zdps-install-profiles-" + Guid.NewGuid().ToString("N"));
        string installA = Path.Combine(directory, "install A 日本語"), installB = Path.Combine(directory, "install B");
        string workingDirectory = Path.Combine(directory, "unrelated hostile cwd");
        string sharedHome = Path.Combine(directory, "shared user context");
        string token = Guid.NewGuid().ToString("N");
        const string sharedSettings = "{\n  \"ProfileId\": \"copied-profile-id\", \"Language\": \"ja-JP\", \"DisplayScale\": 1.25, \"UnknownFutureSetting\": \"keep me\"\n}\n";
        const string otherSettings = "{\r\n  \"ProfileId\": \"copied-profile-id\", \"Language\": \"en-US\", \"DisplayScale\": 1.75, \"UnknownFutureSetting\": \"keep me too\"\r\n}\r\n";
        byte[] settingsA = Encoding.UTF8.GetBytes(sharedSettings);
        byte[] settingsB = Encoding.UTF8.GetBytes(identicalSettings ? sharedSettings : otherSettings);
        string source = NormalizeDirectory(AppContext.BaseDirectory);
        var children = new List<ProfileChild>();
        var expectedA = new List<string>();
        var expectedB = new List<string>();
        try
        {
            foreach (string path in new[] { installA, installB, workingDirectory, sharedHome }) Directory.CreateDirectory(path);
            CopyExecutableFiles(source, installA);
            CopyExecutableFiles(source, installB);
            foreach (string install in new[] { installA, installB })
            {
                File.WriteAllText(Path.Combine(install, ProfileGuardFile), token);
                File.WriteAllText(Path.Combine(install, ReleaseFile), "original");
                Directory.CreateDirectory(Path.Combine(install, "Data"));
            }
            File.WriteAllBytes(SettingsPath(installA), settingsA);
            File.WriteAllBytes(SettingsPath(installB), settingsB);
            // The startup guard permits an empty unrelated Data directory. Keep decoy
            // content outside Data so these fixtures represent permitted normal launches.
            Directory.CreateDirectory(Path.Combine(workingDirectory, "Data"));
            byte[] hostileSentinel = Encoding.UTF8.GetBytes("{\"ProfileId\":\"copied-profile-id\",\"HostileSentinel\":true}\n");
            string hostileSentinelPath = Path.Combine(workingDirectory, "unrelated-data.json");
            File.WriteAllBytes(hostileSentinelPath, hostileSentinel);
            string[] allExecutables = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
                .Where(path => !Path.GetRelativePath(source, path).Split(Path.DirectorySeparatorChar).Contains("Data")).ToArray();
            Check(allExecutables.Length > 5, "The fixture must copy a real executable and its dependencies.");
            var a = Start(installA, "install-a");
            var b = Start(installB, "install-b");
            var readyA = await a.Read("READY");
            var readyB = await b.Read("READY");
            VerifyReady(readyA, installA, expectedA, "original");
            VerifyReady(readyB, installB, expectedB, "original");
            Check(readyA.Pid != readyB.Pid && readyA.Pid != Environment.ProcessId && readyB.Pid != Environment.ProcessId,
                "Independent installation fixtures require separate OS processes.");
            Equal(readyA.WorkingDirectory, readyB.WorkingDirectory);
            Equal(readyA.Home, readyB.Home);
            Equal(readyA.LocalAppData, readyB.LocalAppData);
            Check(readyA.Database != readyB.Database, "Separate installs must resolve different default databases.");
            Check(identicalSettings == (readyA.SettingsHash == readyB.SettingsHash), "Settings fixture does not match its requested scenario.");
            VerifySettings();

            await a.Send("capture concurrent 64 8");
            await b.Send("capture concurrent 64 8");
            await a.Read("CAPTURE_STARTED");
            await b.Read("CAPTURE_STARTED");
            var firstA = await a.Read("CAPTURE_DONE");
            var firstB = await b.Read("CAPTURE_DONE");
            VerifyOverlap(firstA, firstB);
            AddExpected(expectedA, "install-a", "concurrent", 64);
            AddExpected(expectedB, "install-b", "concurrent", 64);
            VerifyTexts(firstA, expectedA);
            VerifyTexts(firstB, expectedB);
            VerifySettings();

            int originalAPid = a.Pid;
            await a.Stop();
            // B keeps capturing while a genuinely new A process restores its own database.
            await b.Send("capture-held across-restart 256 8");
            var bStarted = await b.Read("CAPTURE_STARTED");
            a = Start(installA, "install-a");
            readyA = await a.Read("READY");
            VerifyReady(readyA, installA, expectedA, "original");
            Check(a.Pid != originalAPid && !b.HasExited, "A must restart independently while B remains running.");
            await a.Send("capture after-restart 32 4");
            await a.Read("CAPTURE_STARTED");
            var restartedA = await a.Read("CAPTURE_DONE");
            await b.Send("finish-capture");
            var continuedB = await b.Read("CAPTURE_DONE");
            Check(bStarted.StartedAt <= readyA.EndedAt && readyA.EndedAt < continuedB.EndedAt,
                "A must finish restoring while B's capture interval is still active.");
            VerifyOverlap(restartedA, continuedB);
            AddExpected(expectedA, "install-a", "after-restart", 32);
            int continuedCount = continuedB.Texts.Length - expectedB.Count;
            Check(continuedCount >= 256, "The held writer must capture at least its minimum burst.");
            AddExpected(expectedB, "install-b", "across-restart", continuedCount);
            VerifyTexts(restartedA, expectedA);
            VerifyTexts(continuedB, expectedB);
            VerifySettings();

            // Update and rollback replace actual executable/dependency files in the same
            // installation folder. They intentionally do not replace or migrate Data.
            foreach (var change in new[] { (Release: "updated", Phase: "after-update"), (Release: "original", Phase: "after-rollback") })
            {
                await a.Stop();
                string databaseHash = HashFile(DatabasePath(installA));
                CopyExecutableFiles(source, installA);
                File.WriteAllText(Path.Combine(installA, ReleaseFile), change.Release);
                Equal(databaseHash, HashFile(DatabasePath(installA)));
                VerifySettings();
                Check(!b.HasExited, "Updating A must not stop B.");
                a = Start(installA, "install-a");
                VerifyReady(await a.Read("READY"), installA, expectedA, change.Release);
                await a.Send($"capture {change.Phase} 8 0");
                await a.Read("CAPTURE_STARTED");
                AddExpected(expectedA, "install-a", change.Phase, 8);
                VerifyTexts(await a.Read("CAPTURE_DONE"), expectedA);
                await b.Send("snapshot");
                VerifyTexts(await b.Read("SNAPSHOT"), expectedB);
                VerifySettings();
            }
            await b.Send("capture final 8 0");
            await b.Read("CAPTURE_STARTED");
            AddExpected(expectedB, "install-b", "final", 8);
            VerifyTexts(await b.Read("CAPTURE_DONE"), expectedB);
            await a.Stop();
            await b.Stop();
            VerifySettings();
            var rowsA = ReadRows(DatabasePath(installA));
            var rowsB = ReadRows(DatabasePath(installB));
            Sequence(expectedA, rowsA.Select(row => NotifyNewestChitChatMsgs.Parser.ParseFrom(row.Payload).VRequest.ChatMsg.MsgInfo.MsgText));
            Sequence(expectedB, rowsB.Select(row => NotifyNewestChitChatMsgs.Parser.ParseFrom(row.Payload).VRequest.ChatMsg.MsgInfo.MsgText));
            Equal(4, rowsA.Select(row => row.RunId).Distinct().Count());
            Equal(1, rowsB.Select(row => row.RunId).Distinct().Count());
            Check(rowsA.All(row => row.SenderId == 101) && rowsB.All(row => row.SenderId == 202), "Senders must remain installation-local too.");
            Check(!Directory.EnumerateFiles(workingDirectory, "*", SearchOption.AllDirectories).Any(path => path.EndsWith(".sqlite3", StringComparison.Ordinal)),
                "No history database may be created relative to the hostile working directory.");
            Check(!Directory.EnumerateFiles(sharedHome, "*", SearchOption.AllDirectories).Any(path => path.Contains("history", StringComparison.OrdinalIgnoreCase)),
                "No history database may be created under the shared user context.");
            Console.WriteLine($"  DEFAULT_PROFILES_OK identical_settings={identicalSettings} initial_pids={originalAPid},{b.Pid} rows={rowsA.Count},{rowsB.Count} capture_runs=4,1");

            ProfileChild Start(string install, string marker)
            {
                var child = ProfileChild.Start(install, marker, token, workingDirectory, sharedHome);
                children.Add(child);
                return child;
            }
            void VerifyReady(ProfileReply reply, string install, List<string> expected, string release)
            {
                Equal(NormalizeDirectory(install), NormalizeDirectory(reply.Installation));
                Equal(DatabasePath(install), reply.Database);
                Equal(SettingsPath(install), reply.Settings);
                Equal(workingDirectory, reply.WorkingDirectory);
                Equal(sharedHome, reply.Home);
                Equal(Path.Combine(sharedHome, "LocalAppData"), reply.LocalAppData);
                Equal(release, reply.Release);
                Equal(0, reply.Callbacks);
                VerifyTexts(reply, expected);
            }
            void VerifySettings()
            {
                Sequence(settingsA, File.ReadAllBytes(SettingsPath(installA)));
                Sequence(settingsB, File.ReadAllBytes(SettingsPath(installB)));
                Sequence(hostileSentinel, File.ReadAllBytes(hostileSentinelPath));
                Check(!File.Exists(SettingsPath(workingDirectory)), "The new launch must not create settings in the working directory.");
            }
        }
        finally
        {
            foreach (var child in children) await child.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string SettingsPath(string installation) => Path.Combine(installation, "Data", "Settings.json");
    private static string DatabasePath(string installation) => Path.Combine(installation, "Data", "ChatHistory", "history.sqlite3");
    private static void AddExpected(List<string> messages, string marker, string phase, int count) =>
        messages.AddRange(Enumerable.Range(0, count).Select(i => $"{marker}|{phase}|{i:D4}"));
    private static void VerifyTexts(ProfileReply reply, List<string> expected)
    {
        Sequence(expected, reply.Texts);
        Equal(reply.Texts.Length, reply.Texts.Distinct(StringComparer.Ordinal).Count());
    }
    private static void VerifyOverlap(ProfileReply first, ProfileReply second) =>
        Check(Math.Max(first.StartedAt, second.StartedAt) < Math.Min(first.EndedAt, second.EndedAt),
            "Independent capture/write intervals must overlap.");

    private static void CopyExecutableFiles(string source, string destination)
    {
        foreach (string path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, path);
            if (relative.Split(Path.DirectorySeparatorChar).Contains("Data") || relative is ProfileGuardFile or ReleaseFile) continue;
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target, overwrite: true);
        }
    }

    private sealed class ProfileChild : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task<string> errors;
        public int Pid { get; }
        public bool HasExited => process.HasExited;
        private ProfileChild(Process process)
        {
            this.process = process;
            Pid = process.Id;
            errors = process.StandardError.ReadToEndAsync();
        }
        public static ProfileChild Start(string installation, string marker, string token, string workingDirectory, string sharedHome,
            string mode = "--profile-fixture")
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine test executable.");
            bool dotnetHost = string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase);
            var start = new ProcessStartInfo(dotnetHost ? executable : Path.Combine(installation, Path.GetFileName(executable)))
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = workingDirectory
            };
            if (dotnetHost) start.ArgumentList.Add(Path.Combine(installation, Path.GetFileName(typeof(Program).Assembly.Location)));
            start.ArgumentList.Add(mode);
            start.ArgumentList.Add(marker);
            start.ArgumentList.Add(token);
            start.Environment["HOME"] = sharedHome;
            start.Environment["USERPROFILE"] = sharedHome;
            start.Environment["LOCALAPPDATA"] = Path.Combine(sharedHome, "LocalAppData");
            start.Environment["APPDATA"] = Path.Combine(sharedHome, "AppData");
            start.Environment["XDG_DATA_HOME"] = Path.Combine(sharedHome, "xdg-data");
            foreach (string path in new[] { "LocalAppData", "AppData", "xdg-data" }) Directory.CreateDirectory(Path.Combine(sharedHome, path));
            return new ProfileChild(Process.Start(start) ?? throw new InvalidOperationException("Profile process did not start."));
        }
        public async Task Send(string command)
        {
            await process.StandardInput.WriteLineAsync(command);
            await process.StandardInput.FlushAsync();
        }
        public async Task<ProfileReply> Read(string eventName)
        {
            string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(Timeout);
            if (line == null)
            {
                await process.WaitForExitAsync().WaitAsync(Timeout);
                throw new InvalidOperationException($"Profile {Pid} ended with code {process.ExitCode}: {await errors}");
            }
            var reply = JsonSerializer.Deserialize<ProfileReply>(line) ?? throw new InvalidOperationException("Empty fixture reply.");
            Equal(eventName, reply.Event);
            Equal(Pid, reply.Pid);
            return reply;
        }
        public async Task Stop()
        {
            await Send("stop");
            await Read("STOPPED");
            await ExpectSuccessfulExit();
        }
        public async Task ExpectSuccessfulExit()
        {
            await process.WaitForExitAsync().WaitAsync(Timeout);
            Check(process.ExitCode == 0, $"Profile {Pid} exited with code {process.ExitCode}: {await errors}");
        }
        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            await errors;
            process.Dispose();
        }
    }
}
