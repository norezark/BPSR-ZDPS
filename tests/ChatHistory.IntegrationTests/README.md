# Chat history integration tests

Run from the repository root with the .NET 9 SDK:

```sh
dotnet run --project tests/ChatHistory.IntegrationTests/ChatHistory.IntegrationTests.csproj
```

This platform-independent console runner exits nonzero if any scenario fails. It
links the real `AppPaths`, `ChatManager`, chat data types and `ChatHistoryStore`, and references
the real `BPSR-ZDPSLib` packet/protobuf assembly. Only native-UI settings and the
regex utility are stubbed; SQLite is the production Microsoft.Data.Sqlite 10.0.0
package. It uses isolated temporary databases/installations and removes them after each test.
It does not require a game connection, packet-capture device, or native UI.

The suite checks:

- Capture, clean shutdown, reopen and sender/message restoration
- A separate writer process exits, then a new reader process restores the same
  explicit temporary database with zero live callbacks; no default user profile
  path is used
- Two copied executable installations run concurrently using the production default
  `ChatManager.InitializeHistory()` and real installation-relative `AppPaths`, without
  injecting a database path. Both use the same unrelated working directory and the
  same HOME/USERPROFILE/LOCALAPPDATA/APPDATA/XDG context; one installation path includes
  spaces and Japanese characters
- Both different-settings and byte-identical-settings scenarios, including a shared
  hypothetical copied profile ID, retain disjoint messages/senders and exact settings
  bytes. Timestamped independent capture intervals overlap. One process restarts and
  restores while the other is still capturing; restoration fires zero live callbacks
- Same-folder executable/dependency replacement and rollback preserve each archive,
  settings bytes, and the other running process. A's four capture sessions retain 112
  messages; B's single uninterrupted session retains at least 328, with no cross-profile
  rows. A command barrier holds B's capture open until A has restored and captured,
  so the overlap check does not depend on a particular process startup speed
- The production legacy-data guard permits missing/empty unrelated Data and rejects
  nonempty alternate Data, including identical settings, standalone encounter/preset
  files, and nested directories. Guard calls create, modify, or delete no files
- A regular file blocking the installation's Data/ChatHistory directory produces
  visible restore/write errors and unsaved shutdown, preserves settings and the
  blocker, and creates no alternate database in the shared user context or CWD
- Original game message IDs, metadata, exact protobuf bytes and unknown fields
- No live message callbacks or `/countdown` side effects during restoration/reload
- Current channel, level, include/exclude regex, blocklist and sticker filtering
- Immediate history backfill for tabs added after restoration
- Per-channel display trimming without archive deletion, and cap-growth reloads
- Sender-cache eviction across channels and reconstruction from archived payloads
- Distinct captured events for identical text/packets and reused game IDs/sessions
- Bad saved protobuf bytes, missing sender/sticker data and invalid timestamps: visible
  restore error, valid-row recovery, original bad rows preserved, healthy SQLite
- Concurrent live capture and repeated reloads without loss, duplicates or replay
- Correct capture order and subsequent cap eviction when an older queue-rejected
  live event is merged with later committed events; rejection remains visible
- Shutdown rejection, safe repeated shutdown, and reopening for new capture
- Clean display state when explicitly switching databases
- Chat-disabled capture suppression

These tests cover the manager-to-store path. They do not exercise native window
rendering, application lifecycle hooks, or a real network capture session. Settings
are raw sentinel files: the tests verify exact byte preservation and the real path
resolver, but the stubbed Settings class does not deserialize or save the full app's
settings. The concurrent fixtures deliberately retain the hostile CWD after calling
the real guard, exercising the resolver independently of the app's startup CWD
normalization. Update/rollback replaces copied executable/dependency files using the
same tested build; it does not claim compatibility with a different historical binary
or schema. The blocked-path scenario is an ordinary filesystem obstruction, not an
OS permission-denied/ACL simulation; it changes no permissions. Storage
fault injection and queue/retry mechanics are covered by the separate store tests.

## Independent process fixture modes

The regular suite runs this automatically. For an independently launched
process-restart check, execute these commands sequentially with the same absolute
path under an isolated temporary directory:

```sh
dotnet run --project tests/ChatHistory.IntegrationTests -- --write-fixture /absolute/temp/chat/history.db
dotnet run --project tests/ChatHistory.IntegrationTests -- --read-fixture /absolute/temp/chat/history.db
```

The writer refuses an existing database; the reader requires one. They contain
only fixed synthetic chat, print `WRITER_OK` / `READER_OK` with distinct process
IDs, and never fall back to the normal installation database. On Windows,
use an absolute temporary path such as `%TEMP%\zdps-chat-test\history.db`. Delete
the synthetic database and its temporary directory after a manual fixture run.

The additional `--profile-fixture` and `--blocked-profile-fixture` modes are internal
to the automatic suite. They require a matching random guard token in a copied
temporary installation before touching the production default path, and communicate
with the parent using a line-based command protocol. Do not run them from a normal
installation or manually create their guard files.

## Windows executable-path boundary

In Windows validation of this .NET 9 build, an executable directory containing a
semicolon (`;`) failed before managed test code with CoreCLR error `0x80070057`,
through both the native apphost and `dotnet suite.dll`. These process fixtures use
space/Japanese directory names supported by that runtime; they do not claim that
semicolon-containing executable directories work. Keep the installation/executor
path free of semicolons. `ChatHistory.Tests` separately verifies a semicolon-bearing
SQLite database filename, including connection-string-like text, using real on-disk
write and reopen. The filename case does not require executing from that directory.
