# Actual application settings isolation test

Run from the repository root with the .NET 9 SDK:

```sh
dotnet run --project tests/InstallationSettings.Tests/InstallationSettings.Tests.csproj -c Release
```

This standalone console test **references the actual application project**. It calls
the real `Settings.Load`, `Settings.Save`, `AppPaths`, `Utils.DATA_DIR_NAME`, and
`ChatManager` without substituting any settings, window-settings, or utility types.
It does not call the application's `Main`, create an ImGui/native window, start a
packet-capture session, contact the game, or use the user's installed data.

Two temporary copies of the test executor and its runtime dependencies have truly
different `AppContext.BaseDirectory` roots. Their synthetic `Data/Settings.json`
files have distinct language, capture-device name, chat display cap, blocklist,
window values, tab names/IDs, channel/level/regex filters and selected tab. They
share a working directory with no Data directory and a synthetic user context.
One installation path includes spaces, a semicolon and Japanese characters.

The test verifies:

- Both processes load their actual settings and initialize the default history
  location before a shared barrier releases concurrent actual settings saves
- Each complete serialized settings model survives load/save/reload, except for
  the explicitly edited framerate value; history initialization and messages do
  not change any other setting
- Fresh concurrent restarts restore only the matching installation's synthetic
  messages/sender, respect saved filters, and preserve tab IDs/order/selection
- Restoring history produces no live message callbacks
- Saving just A changes A's settings while every file in B's Data remains
  byte-identical; the new value survives another actual settings load
- No settings or chat database is created in the shared CWD or user context

All fixture writes are confined to a unique temporary test directory, guarded by
an installation marker. The runner never loads/saves settings in its build output
or the real application installation. Runtime copying explicitly excludes Data.
The fixture data is deleted after completion. The exit code is nonzero on failure.

This is complementary to `ChatHistory.IntegrationTests`, whose platform-independent
linked-source harness stubs settings and tests many additional archive scenarios.
This suite proves real settings semantics; it does not exercise startup `Main`,
native `ChatWindow` initialization/rendering, shortcuts, or Windows deployment.
It adds saved tab configs to `ChatManager` explicitly, avoiding native UI execution.
