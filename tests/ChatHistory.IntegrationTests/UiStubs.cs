using BPSR_ZDPS.DataTypes.Chat;
using System.Text.RegularExpressions;

// Only native-UI settings and utilities are replaced. ChatManager, chat models,
// history storage, packet metadata, and all protobuf types are production code.
namespace BPSR_ZDPS
{
    public static class AppState
    {
        public static bool IsChatEnabled { get; set; } = true;
    }

    public static class Utils
    {
        public static bool SafeRegexIsMatch(string text, string pattern)
        {
            try { return Regex.IsMatch(text, pattern); }
            catch (ArgumentException) { return false; }
        }
    }
}

namespace BPSR_ZDPS.DataTypes
{
    public sealed class Settings
    {
        public static Settings Instance { get; set; } = new();
        public ChatSettings Chat { get; set; } = new();
        public TestWindowSettings WindowSettings { get; set; } = new();
    }

    public sealed class TestWindowSettings
    {
        public TestChatWindowSettings ChatWindow { get; set; } = new();
    }

    public sealed class TestChatWindowSettings
    {
        public bool HideStickers { get; set; }
    }
}
