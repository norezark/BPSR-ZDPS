using BPSR_ZDPS;

internal static partial class Program
{
    private static Task LegacySettingsGuard()
    {
        string directory = Path.Combine(Path.GetTempPath(), "zdps-settings-guard-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Fixtures are prepared here. Each production guard invocation must be read-only.
            CheckCase("neither settings file exists", installed: null, legacy: null, rejects: false);
            CheckCase("only installed settings exist", installed: "installed", legacy: null, rejects: false);
            CheckCase("only old launch-folder settings exist", installed: null, legacy: "legacy", rejects: true);
            CheckCase("copied settings cannot establish complete profile identity", installed: "copied\r\n", legacy: "copied\r\n", rejects: true);
            CheckCase("settings conflict", installed: "installed", legacy: "legacy", rejects: true);
            CheckCase("even whitespace differences need resolution", installed: "{}\r\n", legacy: "{}\n", rejects: true);
            CheckCase("the working directory is the installation", installed: "same path", legacy: null, rejects: false, sameDirectory: true);
            CheckCase("empty unrelated Data is safe", installed: "installed", legacy: null, rejects: false, legacyDataEntry: "");
            CheckCase("legacy database without Settings must be resolved", installed: "installed", legacy: null, rejects: true, legacyDataEntry: "encounters.db");
            CheckCase("legacy preset without Settings must be resolved", installed: null, legacy: null, rejects: true, legacyDataEntry: "presets/custom.json");
            CheckCase("a legacy empty subdirectory is still ambiguous data", installed: null, legacy: null, rejects: true, legacyDataEntry: "presets/");
            return Task.CompletedTask;

            void CheckCase(string name, string? installed, string? legacy, bool rejects, bool sameDirectory = false, string? legacyDataEntry = null)
            {
                string caseDirectory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
                string installation = Path.Combine(caseDirectory, "installation");
                string workingDirectory = sameDirectory ? installation : Path.Combine(caseDirectory, "launch");
                Directory.CreateDirectory(caseDirectory);
                if (installed != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath(installation))!);
                    File.WriteAllText(SettingsPath(installation), installed);
                }
                if (legacy != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath(workingDirectory))!);
                    File.WriteAllText(SettingsPath(workingDirectory), legacy);
                }
                if (legacyDataEntry != null)
                {
                    string entry = Path.Combine(workingDirectory, "Data", legacyDataEntry);
                    if (legacyDataEntry == "" || legacyDataEntry.EndsWith('/')) Directory.CreateDirectory(entry);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
                        File.WriteAllText(entry, "legacy data preserved");
                    }
                }
                string[] before = SnapshotTree(caseDirectory);
                bool rejected = false;
                try
                {
                    // Include redundant path segments to exercise canonical comparison.
                    AppPaths.ValidateLegacySettingsLocation(Path.Combine(installation, "."), Path.Combine(workingDirectory, "."));
                }
                catch (InvalidOperationException exception)
                {
                    rejected = true;
                    Check(exception.Message.Contains(SettingsPath(installation), StringComparison.Ordinal), "The guard must identify installed settings.");
                    Check(exception.Message.Contains(SettingsPath(workingDirectory), StringComparison.Ordinal), "The guard must identify old launch-folder settings.");
                }
                Check(rejected == rejects, "Unexpected settings-guard outcome: " + name);
                Sequence(before, SnapshotTree(caseDirectory));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static string[] SnapshotTree(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(directory, path) + (Directory.Exists(path) ? "/" : ":" + HashFile(path))
                + ":modified=" + File.GetLastWriteTimeUtc(path).Ticks)
            .Order(StringComparer.Ordinal).ToArray();

    private static async Task BlockedInstallationProfile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "zdps-blocked-profile-" + Guid.NewGuid().ToString("N"));
        string installation = Path.Combine(directory, "installation");
        string workingDirectory = Path.Combine(directory, "unrelated working directory");
        string sharedHome = Path.Combine(directory, "shared user context");
        string token = Guid.NewGuid().ToString("N");
        ProfileChild? child = null;
        try
        {
            CopyExecutableFiles(NormalizeDirectory(AppContext.BaseDirectory), installation);
            Directory.CreateDirectory(Path.Combine(installation, "Data"));
            Directory.CreateDirectory(workingDirectory);
            Directory.CreateDirectory(sharedHome);
            File.WriteAllText(Path.Combine(installation, ProfileGuardFile), token);
            File.WriteAllText(Path.Combine(installation, ReleaseFile), "original");
            File.WriteAllText(SettingsPath(installation), "{\"Language\":\"ja-JP\",\"UnknownValue\":\"preserve exactly\"}\r\n");
            File.WriteAllText(Path.Combine(installation, "Data", "ChatHistory"), "blocker preserved");
            string settingsHash = HashFile(SettingsPath(installation));
            string[] beforeData = SnapshotTree(Path.Combine(installation, "Data"));
            child = ProfileChild.Start(installation, "blocked", token, workingDirectory, sharedHome, "--blocked-profile-fixture");
            var result = await child.Read("BLOCKED_OK");
            await child.ExpectSuccessfulExit();
            Equal(DatabasePath(installation), result.Database);
            Equal(settingsHash, result.SettingsHash);
            Equal(1, result.Callbacks);
            Sequence(beforeData, SnapshotTree(Path.Combine(installation, "Data")));
            Check(!Directory.EnumerateFiles(directory, "*.sqlite3", SearchOption.AllDirectories).Any(),
                "Storage failure must not silently fall back to another history database.");
            Check(!Directory.EnumerateFiles(workingDirectory, "*", SearchOption.AllDirectories).Any(),
                "Storage failure must not modify the working directory.");
            Check(!Directory.EnumerateFiles(sharedHome, "*", SearchOption.AllDirectories).Any(),
                "Storage failure must not write into the shared user profile.");
        }
        finally
        {
            if (child != null) await child.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
