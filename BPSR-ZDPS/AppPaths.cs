namespace BPSR_ZDPS;

/// <summary>
/// Each portable installation owns its Data directory. No copyable profile ID or user-profile
/// directory is used, so copying settings to another installation cannot share its archive.
/// </summary>
public static class AppPaths
{
    public static string InstallationDirectory => Path.GetFullPath(AppContext.BaseDirectory);
    public static string DataDirectory => Path.Combine(InstallationDirectory, "Data");
    public static string SettingsFilePath => Path.Combine(DataDirectory, "Settings.json");
    public static string ChatHistoryDatabasePath => Path.Combine(DataDirectory, "ChatHistory", "history.sqlite3");

    /// <summary>
    /// Older builds resolved settings against the launch working directory. Never silently
    /// abandon those settings when a shortcut has a custom Start in directory. No files move.
    /// </summary>
    public static void ValidateLegacySettingsLocation()
    {
        ValidateLegacySettingsLocation(InstallationDirectory, Environment.CurrentDirectory);
    }

    public static void ValidateLegacySettingsLocation(string installationDirectory, string workingDirectory)
    {
        var installedSettings = Path.GetFullPath(Path.Combine(installationDirectory, "Data", "Settings.json"));
        var legacySettings = Path.GetFullPath(Path.Combine(workingDirectory, "Data", "Settings.json"));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(installedSettings, legacySettings, comparison)) return;
        string legacyData = Path.GetDirectoryName(legacySettings)!;
        // Equal Settings.json alone cannot establish that encounter databases, presets or
        // custom assets belong to the same profile. Any nonempty old Data root is ambiguous.
        if (!Directory.Exists(legacyData) || !Directory.EnumerateFileSystemEntries(legacyData).Any()) return;
        throw new InvalidOperationException(
            "ZDPS found a Data folder in a different launch location. No settings or data were changed.\n\n" +
            $"This installation's Data folder: {Path.GetDirectoryName(installedSettings)}\nPrevious launch-folder Data: {legacyData}\n\n" +
            "Confirm which Data folder belongs to this copy before starting it. Keep each copy's Data folder separate, " +
            "and set the shortcut's Start in folder to that copy's installation folder. " +
            "If the launch-folder data is the intended profile, back up and migrate its settings, presets and databases into this installation before retrying.");
    }
}
