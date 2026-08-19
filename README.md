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
| `src/DBsync.Tray` | `net6.0-windows` (WPF) | The per-user tray app: tray icon and flyout — see [Tray app](#tray-app). |

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

## Tray app

The per-user half: a notification-area icon and the flyout it opens. Run
`DBsync.Tray.exe` and it sits in the tray — Windows 11 hides new tray icons in the overflow by
default, so drag it out of the `^` menu to keep it visible.

Left-click the icon to toggle the flyout; right-click for Open / Pause / Quit. The flyout dismisses
when it loses focus, like the shell's own flyouts.

**Development flags:** `--show` opens the flyout at launch, `--pin` keeps it open when it loses
focus (for inspection and screenshots), `--wizard` opens the add-pair wizard, `--gallery` opens
the token gallery, `--audit` runs the theme audit headless.

### The pair wizard

Three steps — local folder, destination, sync options — reached from "Add folder pair". Step 2's
validation line is the service's own copy, rendered verbatim: the probe runs service-side on
purpose, because that is the account that will do the syncing, and a share this user can reach may
be invisible to LocalSystem.

**Mapped drives resolve to UNC before saving.** Drive letters are per-session and do not exist for
the service, so a pair saved as `Z:\team\projects` would sit in Waiting forever while reporting a
perfectly healthy share as unreachable. The wizard rewrites the letter to its UNC target
(`WNetGetConnection`), and `SyncEngine.Validate` refuses any destination on a drive root the
service itself cannot see, so the CLI and hand-edited config cannot get past it either. A local
second disk is a legitimate destination and is still allowed — the test is "can this process see
that root", not "is it a drive letter".

### Live state

The flyout paints from `GetStateAsync()` and then follows `PairChanged` / `StateChanged` push
events — it never polls. `ServiceConnection` wraps `DBsyncClient` with a reconnect ladder, because
the client deliberately does not reconnect itself: the tray app is a per-user process and the
service is a machine-level one, so the service can stop, crash or be upgraded underneath it. On
every successful connect it re-subscribes and pulls a full state, so killing and restarting the
service leaves the flyout correct with no user action.

While disconnected the header says so rather than falling back to "Everything in sync" — the
calmest words it has would be the worst possible default when nothing is syncing. The full banner
treatment is issue #12.

Status-dependent colour, icon and tag styling live in DataTriggers in the row template, not in the
view model, so they stay `DynamicResource` and follow an appearance change. `PairViewModel` is a
projection only: `Detail` and the header line arrive from the service already worded.

### The activity window

The audit trail, reached from either Activity button. Rows arrive from `GetActivityAsync` and then
append live from the `LogAppended` push event — a sync happening while the window is open shows up
without a re-query.

Filters (pair, event kind, 24 hours / 7 days / 30 days) are beyond the mock, which shows a fixed
24-hour view. The header counters are scoped to whatever is selected, including the subline: saying
"across 4 folder pairs" while showing one pair's rows would misdescribe the numbers next to it.

Paging is server-side via `ActivityQuery.Offset`, fetched as the list nears its end, and the list
virtualises with recycling — a month of history never crosses the pipe at once. `ActivityEntry.Message`
carries the Win32 text on failures; it is surfaced in the row tooltip rather than a column, since it
is long and rare.

### The conflict dialog

Reached from a Conflict row in the flyout, or from "Review conflicts" in the activity window. It
works through open conflicts one at a time, so every decision is made about a named file.

This is the only surface that decides which copy of a file survives, so it is deliberately
conservative: nothing resolves until a button is pressed, the primary action always names the copy
it will keep ("Keep this PC" / "Keep network copy"), the apply-to-all checkbox states its exact
count rather than an open-ended "all" and hides when there is nothing else to apply to, and Escape
dismisses without deciding — it is bound to neither resolution, so a stray keypress cannot choose.

The service parks conflicts (an open conflict is excluded from reconciliation until resolved), so
neither copy moves while the dialog is open and the UI needs no guard of its own.

"Keep both" renames this PC's copy to `<name> (<user>, <machine>).<ext>` and leaves both files on
both sides — no data is lost either way.

The design's backdrop covers "the whole app surface". Here it dims whichever window opened the
dialog; when it is opened from the tray there is no app surface to dim, and darkening the user's
whole desktop for a file decision would be more intrusive than the design intends.

### Notifications

Every string is in `Notifications/ToastCopy.cs`, transcribed from the table in
[docs/README.md](docs/README.md) §5 — one file to check the words against the design.

Toasts go to the shell via `ToastNotificationManagerCompat`, which registers the AUMID an
unpackaged app needs. **`Show()` succeeding does not mean the toast was shown**: when notifications
are off for the app, the user, or by policy, the shell accepts it and silently discards it —
it never even reaches the notification centre. So delivery is decided by asking
`ToastNotifier.Setting`, and anything the shell will not display falls back to the in-app card the
design specifies, stacked bottom-right above the flyout. That check runs per notification, so
turning notifications back on takes effect immediately.

"Catching up on N queued changes" uses `ServiceState.QueuedChanges` rather than an invented number.
The service snapshots the backlog *before* releasing the workers — a moment later they are draining
it — and counts all three places work sits, since while paused nearly all of it is in the channel.

### Theming

The Nocturne token set as WPF resource dictionaries, plus Light / Dark / Match Windows switching
that follows the OS live:

- `Themes/Ramps.xaml`, `Metrics.xaml`, `Typography.xaml` and `Controls.xaml` are
  mode-independent. Only `Theme.Dark.xaml` / `Theme.Light.xaml` differ, and `ThemeManager` swaps
  that one dictionary in place — every `DynamicResource` in the tree repaints, and no window has
  to know a theme changed. **Reference tokens with `DynamicResource`, never `StaticResource`**;
  a static reference resolves once at load and will not follow a swap.
- `WindowsTheme` reads `AppsUseLightTheme` (the app preference, deliberately not
  `SystemUsesLightTheme`) and re-themes on `SystemEvents.UserPreferenceChanged`, no restart.
- Appearance persists per user in `%APPDATA%\DBsync\tray.json` — not in the service's
  machine-level store, which is why there is no `appearance` field in the IPC contract.
- `Text/Tracking.cs` implements letter-spacing, which WPF has no equivalent for, by rebuilding a
  `TextBlock`'s inlines around zero-width spacers. Used on kickers, table headings and h4 only.
- `Icons/` maps each icon the design names to a Segoe Fluent Icons codepoint via an `IconKey`
  enum, so a font change is one edit.

Two things translate rather than port. CSS `box-shadow` becomes a hairline border plus a
`DropShadowEffect`, so a surface at a given elevation needs both halves. The fading rule is three
strips rather than a gradient, because a gradient cannot express "48px from both ends" at an
unknown width.

**Checking it.** `DBsync.Tray.exe --audit` compares every resolved role brush against the table
in [Theming](#theming--light-dark-and-match-windows) in both palettes and exits non-zero on a
mismatch. Running the app with no arguments opens a gallery window — a development harness
showing the tokens, type scale, icon sheet and control styles with a live mode switcher. Both
disappear once the real surfaces land.

## Not built yet

Deliberately out of scope for this pass — the design covers them and the contract is ready:

- **CSV export and per-pair settings** (#7, #9). Those buttons are present and styled but say which
  issue delivers them.
- **Real Windows toasts** via `ToastNotificationManager` (a tray-app concern; the service already
  pushes the events that trigger them).
- **CSV export** for the activity log — `GetActivity` returns the rows; formatting is the
  client's job.
- Per the design's own list: first-run setup, credential-failure re-prompt, disk-full and
  permission-denied surfaces, and a service-not-running banner. The engine reports the underlying
  conditions today (`Failed` activity rows carry the Win32 message); presenting them is UI work.

## Known constraints

- **Inter and Phosphor are not bundled.** The design specifies Inter for type and Phosphor
  shipped as vector assets; the app uses the system font stack (Segoe UI) and Segoe Fluent Icons
  instead, so no third-party assets enter the repo. The type scale, weights and spacing are
  correct but the letterforms are not, and the icon language differs — `warning-diamond` renders
  as a triangle and `cloud-slash` as a wifi-off glyph, since neither has a counterpart. This is a
  deliberate trade against the docs' high-fidelity brief. Dropping Inter's `.ttf` files into
  `src/DBsync.Tray/Assets/Fonts` switches the type over with no code change.
- The service runs as **LocalSystem**, which has no network identity. UNC shares that need
  authentication require credentials on the pair (`--user`/`--password`), or reconfiguring the
  service to run as a domain account with access.
- **Mapped drive letters** (`Z:\...`) are per-session and do not exist in the service's session.
  The wizard resolves them to UNC before saving and the service rejects what it cannot see — but
  note that running `DBsync.Service.exe` directly from a console runs it as *you*, where mapped
  drives are visible, so that guard only bites once the service is installed under LocalSystem.
- Volume shadow copy needs a local NTFS volume and shadow storage available. `TryCreate` returns
  null and the file is logged as skipped rather than failing when it cannot snapshot.
- Content comparison is size + mtime, not a hash. Two edits that produce identical size *and*
  timestamps within 2 s are treated as the same content.
