# Chat history regression tests

Run with the .NET 9 SDK:

```sh
dotnet run --project tests/ChatHistory.Tests/ChatHistory.Tests.csproj -c Release
```

This isolated console suite links the production store directly. It uses the application's
existing Microsoft.Data.Sqlite 10.0.0 dependency, synthetic metadata and byte arrays, and
per-test temporary directories. No packet capture, GUI, network service, or personal chat
history is needed. Exit code zero means every assertion passed.

Covered behavior:

- Exact serialized payload and metadata survive shutdown/reopen; caller/read mutations do not
- Per-channel display limits retain every archived row
- Equal text and reused original game message IDs remain separate captured events
- Retrying a stable event ID is idempotent, including after reopening; conflicting data is reported
- Concurrent producers/readers, pending overlays, and historical retries preserve ordering and IDs
- The queue is bounded, rejected writes are explicit and sticky, and failed writes retry
- A successful read cannot clear a write error; released SQLite locks recover automatically
- Cancellation of a flush does not stop writes
- Shutdown drains healthy writes, rejects later appends, and reports unsaved entries on failure
- Corrupt, future, foreign, malformed, or unexpectedly extended schemas are not replaced or modified
- Temporary directory/open failures recover without losing accepted pending events

## Durability boundary

`Append` / `TryAppend` report acceptance into bounded in-memory storage, not a disk commit.
Generate a fresh `EventId` for each captured event; keep that ID unchanged for retries. Only
successful `Flush` / `FlushAsync` or `ShutdownAsync` confirms committed accepted events.
SQLite uses WAL and FULL synchronization. A forced process termination can lose queued data.
A full queue returns false (or `QueueFull`) and increments `RejectedCount`; accepted pending
entries remain queued during failures. There is no delete, purge, or database replacement path.

`Flush` covers events accepted before that call. Its cancellation cancels the wait, not the
writer. `ShutdownAsync` stops acceptance and shares a single shutdown among concurrent callers;
the first timeout is used. On failure the status retains the number of unsaved pending events.
`Dispose` makes the same attempt with a five-second drain budget (an in-flight SQLite operation
can take its additional one-second busy timeout). Always inspect the status/result if shutdown
success must be presented to a user. `TryReadRecent` performs disk I/O and belongs on a background
thread; `Status` and append do not wait for disk I/O.
