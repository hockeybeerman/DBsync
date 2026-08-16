# DBsync — Windows sync service

The machine-level half of DBsync: a Windows Service that owns the sync engine, plus the IPC
contract the tray app talks to it over. The design this implements is in [docs/README.md](docs/README.md)
and [docs/DBsync.dc.html](docs/DBsync.dc.html).

The tray UI is **not** in this repo yet — see [Not built yet](#not-built-yet).

## Layout

| Project | Target | What it is |
| --- | --- | --- |
| `src/DBsync.Contracts` | `net6.0` | DTOs, the named-pipe wire protocol, and `DBsyncClient`. Referenced by the service, the CLI, and (later) the tray app. |
| `src/DBsync.Service` | `net6.0-windows` | The service: sync engine, config store, SQLite history, pipe server. |
| `src/DBsync.Cli` | `net6.0-windows` | `dbsync.exe` — drives the service from a terminal. Doubles as a worked example of the contract. |

## Build and install

```powershell
dotnet build DBsync.sln -c Release

# From an elevated prompt — publishes to %ProgramFiles%\DBsync and registers the service
.\install\Install-DBsyncService.ps1
```

To run it in the foreground instead (no install, no admin needed), just start the exe:

```powershell
.\src\DBsync.Service\bin\Release\net6.0-windows\DBsync.Service.exe --verbose
```

Uninstall with `.\install\Uninstall-DBsyncService.ps1 [-RemoveData]`.

## Using it

```powershell
dbsync add --name Projects --local C:\Users\dana\Projects --share \\NAS-01\team\projects `
           --exclude "*.tmp,~$*,node_modules/" --limit "8 MB/s" --versions 5
dbsync status
dbsync watch                      # live push-event stream
dbsync conflicts
dbsync resolve --conflict 1 --keep both
dbsync pause / dbsync resume
```

`dbsync` with no arguments prints the full verb list. `--json` on `status`, `activity`,
`conflicts` and `probe` gives machine-readable output.

## How the engine works

One `PairWorker` per folder pair, each with a single reconcile loop. Everything that touches a
pair's state runs on that loop, so change detection needs no locking.

**Change detection** is baseline-relative, not timestamp-relative. Each file has a row recording
the size and mtime both sides had at their last agreement (`file_state` in SQLite). A side
"changed" when it differs from that baseline, which is what lets the engine tell an edit from a
deletion, and a one-sided edit from a genuine conflict. Timestamps are compared to whole seconds
with a 2-second tolerance, because SMB and FAT round differently.

| Local | Share | Two-way | Push | Pull |
| --- | --- | --- | --- | --- |
| changed | unchanged | send | send | ignore |
| unchanged | changed | receive | ignore | receive |
| both changed, same content | — | adopt as baseline | adopt | adopt |
| both changed, both exist | — | **conflict** | local wins | share wins |
| edited one side, deleted other | — | resurrect the edit | local wins | share wins |
| deleted both | — | drop the baseline row | drop | drop |

Resurrecting on edit-vs-delete is deliberate: losing an edit is worse than resurrecting a file
the user can delete again.

**Everything else:**

- **Watchers** — one `FileSystemWatcher` per root (the share root too, unless the pair is
  push-only), 64 KB buffer, recursive. A watcher error means events were dropped, so it forces a
  full rescan. A periodic sweep (5 min default) covers UNC watchers that some NAS firmware never
  fires.
- **Debounce** — changes are coalesced per path over a quiet period (750 ms default), so an
  editor's write/rename/touch burst becomes one copy, after the writer is finished.
- **Copies** are atomic: bytes land in a `.dbsync-part` temp beside the destination and are
  renamed into place, so an interrupted transfer never leaves a truncated file for the far side
  to adopt. Source mtime is carried across so the copy is not seen as a change and echoed back.
- **Locked files** — a sharing violation is retried; with `--vss` the source is read through a
  `Win32_ShadowCopy` snapshot instead. If neither works the file is logged `Locked/Skipped`
  rather than failing the batch.
- **Versioning** — the copy about to be overwritten (or deleted) is moved to
  `<root>\.dbsync-versions\<dir>\<timestamp>__<name>` and pruned to `--versions`.
- **Throttling** — a token bucket on outbound bytes only; the wizard's limit is an *upload*
  limit, so inbound copies run free.
- **Unreachable shares** — status goes `Waiting` with a live countdown, retrying on a
  5/10/20/40/60/120/300-second ladder. Coming back triggers a full rescan.
- **Conflicts** are parked, not resolved. The file is excluded from reconciliation until a
  decision arrives, so neither copy moves. `--keep both` renames this PC's copy to
  `<name> (<user>, <machine>).<ext>` and keeps both files on both sides.

## Storage

Everything the service owns is under `%ProgramData%\DBsync`:

| Path | Contents |
| --- | --- |
| `config.json` | Folder pairs, exclusions, limits, global pause. Written atomically; an unreadable file is quarantined rather than silently overwritten. |
| `dbsync.db` | SQLite (WAL): `activity` (the history window), `conflict` (open + resolved), `file_state` (the sync baseline). |
| `logs\dbsync-YYYYMMDD.log` | Day-rolling service log, kept 14 days. |

UNC credentials are **never** in config.json — they go to Windows Credential Manager under
`DBsync:<pairId>`, and are used to open a `WNetAddConnection2` session for the share.

## IPC contract

Newline-delimited UTF-8 JSON over `\\.\pipe\DBsync.v1`. One envelope in both directions
(`IpcMessage`: request / response / event). A single connection carries request-response traffic
*and* the push-event stream, so a client that reconnects after a service restart gets a complete
picture with no extra handshake.

The pipe DACL grants Authenticated Users read/write — any interactive session can drive its own
tray app — while `CreateNewInstance` is reserved to the service's own account, LocalSystem and
Administrators, so a client cannot stand up an impostor pipe.

`DBsyncClient` in `DBsync.Contracts` is the client half:

```csharp
await using var client = await DBsyncClient.ConnectAsync();
client.EventReceived += message => { /* PairChanged, ConflictRaised, LogAppended, ... */ };
await client.SubscribeAsync();

var state = await client.GetStateAsync();   // pairs + StatusLine, ready to render
```

Requests: `Ping`, `GetState`, `AddPair`, `UpdatePair`, `DeletePair`, `SetPairEnabled`,
`PauseAll`, `ResumeAll`, `SyncNow`, `GetActivity`, `GetActivitySummary`, `GetConflicts`,
`ResolveConflict`, `ProbeDestination`, `StoreCredentials`, `Subscribe`.

Events: `StateChanged`, `PairChanged`, `ConflictRaised`, `ReachabilityChanged`, `LogAppended`.

### Copy lives on the service side

`StatusText` produces the product's exact strings — "Everything in sync", "2 files need your
attention", "Up to date · checked 2 minutes ago", "Sending build-notes.md — 148 of 240 files",
"Share unreachable — retrying in 40s", "Paused — resume to continue". `ServiceState.StatusLine`
and `FolderPair.Detail` arrive ready to render, so every client shows the same words without
re-deriving them.

## Not built yet

Deliberately out of scope for this pass — the design covers them and the contract is ready:

- **The tray app.** All five surfaces (flyout, pair wizard, activity window, conflict dialog,
  toasts), the Nocturne tokens, and light/dark/Match-Windows theming. Per the design, appearance
  is a *per-user* preference and belongs in the tray app's own config, not in the service's
  machine-level store — there is deliberately no `appearance` field in the contract.
- **Real Windows toasts** via `ToastNotificationManager` (a tray-app concern; the service already
  pushes the events that trigger them).
- **CSV export** for the activity log — `GetActivity` returns the rows; formatting is the
  client's job.
- Per the design's own list: first-run setup, credential-failure re-prompt, disk-full and
  permission-denied surfaces, and a service-not-running banner. The engine reports the underlying
  conditions today (`Failed` activity rows carry the Win32 message); presenting them is UI work.

## Known constraints

- The service runs as **LocalSystem**, which has no network identity. UNC shares that need
  authentication require credentials on the pair (`--user`/`--password`), or reconfiguring the
  service to run as a domain account with access.
- **Mapped drive letters** (`Z:\...`) are per-session and do not exist in the service's session.
  Prefer the UNC path the letter points at; the wizard's "Mapped drive" option should resolve to
  UNC before saving.
- Volume shadow copy needs a local NTFS volume and shadow storage available. `TryCreate` returns
  null and the file is logged as skipped rather than failing when it cannot snapshot.
- Content comparison is size + mtime, not a hash. Two edits that produce identical size *and*
  timestamps within 2 s are treated as the same content.
