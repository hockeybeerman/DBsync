# Handoff: DBsync — Windows tray file-sync service

## Overview
DBsync is a Windows background service plus a tray-resident UI that keeps local folders in sync
with network shares. The user configures **folder pairs**: one local path ⇄ one network destination
(UNC path or mapped drive letter). Sync is two-way by default and driven by a realtime filesystem
watcher. The UI surfaces covered by this design are the tray flyout, the add/edit folder-pair
wizard, the activity & history window, the conflict-resolution dialog, and Windows toast
notifications.

Audience: mixed — a simple default path with an "Advanced options" disclosure for admins.

## About the Design Files
The files in this bundle are **design references created in HTML** — prototypes showing intended
look and behavior. They are not production code to lift directly.

The task is to **recreate these designs in the target environment**: a Windows desktop app (WPF,
WinUI 3, or WinForms with a modern renderer) fronting a Windows Service, or an Electron/Tauri shell
if the team prefers web tech. Use whatever styling and component patterns the target codebase
already has; where none exist, the tokens in this document are the source of truth.

`DBsync.dc.html` is one file containing all five surfaces, positioned inside a simulated 1440×900
Windows desktop with a taskbar. In the real product each surface is a separate window/popup — the
desktop frame, wallpaper grid, desktop icons, and taskbar are **prototype scaffolding only** and
must not be implemented.

## Fidelity
**High-fidelity.** Colors, typography, spacing, radii, shadows, copy, and interaction states are
final. Recreate them precisely using the tokens listed under *Design Tokens*.

## Architecture context (what the UI implies)
- A **Windows Service** (or long-running background worker) owns the sync engine: one
  `FileSystemWatcher` per pair root (recursive), a debounced change queue, per-pair worker,
  retry-with-backoff when a share is unreachable, and a conflict store.
- A **tray application** (per user session) renders all UI and talks to the service over
  named pipes / gRPC-over-named-pipes. It must survive service restarts and reflect live state.
- Credentials for UNC shares are stored in **Windows Credential Manager**, never in app config.
- **Appearance** (light / dark / match Windows) is a per-user tray-app preference, persisted in the
  user's own config — not machine-level service config.
- Locked-file copying uses **Volume Shadow Copy** when the advanced option is enabled.
- Config (pairs, exclusions, limits) lives in a machine-level store; the tray app only reads/writes
  it through the service.

## Screens / Views

### 1. Tray flyout (`data-screen-label="Tray flyout"`)
**Purpose:** at-a-glance status of every folder pair; entry point to everything else.

**Layout:** 384px wide panel, `border-radius: var(--radius-lg)` (14px), background `--color-bg`
(#161826), `box-shadow: var(--shadow-lg)`. Anchored bottom-right, 16px from the right edge and
16px above the taskbar. Vertical flex, no internal scroll in the mock (real app: scroll past ~6
pairs, max height ~560px). Entry animation: `translateY(10px)` + opacity 0 → rest, 140ms ease-out.

**Header** (`padding: 11.2px 11.2px 8.4px`, horizontal flex, gap 8.4px):
- Phosphor `shuffle-simple` (fill), 18px, `--color-accent` (#9184d9). Rotates continuously
  (`3.2s linear infinite`) while any pair is syncing; static when paused.
- Title "DBsync" — Inter 500, 15px.
- Status line under the title, 12px. Three states, exact copy:
  - "Everything in sync" — color `--color-neutral-400` (#b2b6ca)
  - "2 files need your attention" — color `--color-accent` (#9184d9)
  - "All syncing paused" — color `color-mix(in srgb, #e9e9ed 55%, transparent)`
- Two 30×30 icon buttons (`.btn.btn-icon`): `clock-counter-clockwise` (Activity),
  `gear-six` (Settings — not designed; open a settings window).

**Appearance row** (between the pair list and the footer, `padding: 8.4px 11.2px 0`): label
"Appearance" (13px) with a live 11px muted subline — "Matching Windows — light",
"Matching Windows — dark", "Always light" or "Always dark" — and a right-aligned `.seg` of three
icon-only options (`padding: 6px 9px`, 14px glyphs, `title` as the accessible label):
`sun` = Light, `moon` = Dark, `desktop-tower` = Match Windows. In the shipped app this control
also belongs in the settings window; the flyout copy is the same either way.

**Pair rows** (one per folder pair, full-width button, `padding: 11px 16px`, or `8px 14px` in
compact density; `border-left: 2px solid <status edge>`; hover
`background: color-mix(in srgb, #e9e9ed 5%, transparent)`):
- Line 1: status icon 15px + pair name (14px) + status tag right-aligned.
- Line 2: monospace 11px at 55% text opacity — local path, Phosphor `arrows-left-right` 12px,
  share path. Local truncates at 130px, share at 160px, ellipsis.
- Line 3: detail text, 11px at 62% text opacity.
- Line 4 (syncing only): 3px progress track, `border-radius: 2px`, background
  `--color-neutral-900` (#292b31); fill `--color-accent` at the pair's percentage, plus a 30%-wide
  highlight sweep (`transparent → --color-accent-200 → transparent`) animating
  `translateX(-100%) → translateX(280%)`, 1.6s linear infinite.

Status → icon / tag / left edge:
| Status | Icon (Phosphor) | Tag | Tag class | Left edge |
| --- | --- | --- | --- | --- |
| Syncing | `arrows-clockwise` | "Syncing" | `.tag-accent` (bg #423a6a, text #f5f4ff) | #5d5294 |
| In sync | `check` | "In sync" | `.tag-neutral` (bg #3f424d, text #f3f5fe) | transparent |
| Conflict | `warning-diamond` | "Conflict" | `.tag-outline` (1px #9184d9 border, #9184d9 text) | #9184d9 |
| Waiting (share offline) | `cloud-slash` | "Waiting" | `.tag-neutral` | transparent |
| Paused (global) | `pause` | "Paused" | `.tag-neutral` | transparent |

**Footer** (`padding: 11.2px`, gap 5.6px): `.btn.btn-primary` "Add folder pair" (`plus` icon),
`.btn.btn-secondary` "Pause all" / "Resume all" (`pause` / `play` icon), `.btn.btn-ghost`
"Activity" pushed right.

**Seed data used in the mock (replace with live state):**
1. Projects — `C:\Users\dana\Projects` ⇄ `\\NAS-01\team\projects` — Syncing 62% —
   "Sending build-notes.md — 148 of 240 files"
2. Invoices — `D:\Finance\Invoices` ⇄ `Z:\finance\invoices` — In sync —
   "Up to date · checked 2 minutes ago"
3. Q3 Reports — `C:\Users\dana\Reports` ⇄ `\\NAS-01\finance` — Conflict —
   "2 files changed in both places"
4. Photo drops — `E:\Shoots\2026` ⇄ `\\NAS-01\media\shoots` — Waiting —
   "Share unreachable — retrying in 40s"

### 2. Add folder pair wizard (`data-screen-label="Pair wizard"`)
**Purpose:** create (or edit) one folder pair in three steps.

**Layout:** 520px window, `--radius-lg`, background `--color-bg`, `--shadow-lg`, z-index above the
flyout. Title bar: `--color-surface` (#232532), `padding: 8.4px 11.2px`, `arrows-left-right` accent
icon, "Add folder pair" (Inter 500, 13px), 30×30 close button (`x`).

**Step indicator:** three equal columns, each a 2px bar + 11px label
("Local folder", "Destination", "Sync options"). Completed/current: bar `--color-accent`, label
`--color-accent-300` (#d2cefd). Upcoming: bar `--color-neutral-800` (#3f424d), label 55% text.

**Body** `padding: 16.8px 22.4px 22.4px`, vertical flex gap 11.2px.

*Step 1 — "Which local folder?"* (`h4`, 20px)
- `.field` "Local path" + `.input` (default `C:\Users\dana\Projects`) + `.btn.btn-secondary`
  "Browse" (`folder-open`) that opens the native folder picker.
- "RECENT" label (11px, uppercase, letter-spacing 0.06em) + monospace 12px secondary buttons:
  `C:\Users\dana\Documents`, `D:\Design`, `C:\Work\Contracts` (real app: MRU list).
- Helper copy, 12px muted: "Subfolders are included. DBsync watches this folder and syncs changes
  as they happen."

*Step 2 — "Where on the network?"*
- `.seg` segmented control: "UNC path" (`network` icon) / "Mapped drive" (`hard-drive` icon).
  Switching swaps the default value between `\\NAS-01\team\projects` and `Z:\team\projects`.
- `.field` labelled "UNC path" or "Mapped drive path" + monospace `.input`.
- UNC only: a `--color-surface` panel (`padding: 11.2px`, `--radius-md`) with a 2-column grid —
  "Username" (`CORP\dana`), "Password" (masked) — and a full-width checkbox
  "Store credentials in Windows Credential Manager" (checked by default).
- Validation line, 12px `--color-accent-300`, `check-circle` icon:
  "Reachable — 2.1 TB free, write access confirmed". Real app: test connect + write probe on blur,
  and show a failure variant (accent `x-circle`, message from the Win32 error).

*Step 3 — "How should it sync?"*
- Three `.radio` options with 12px muted hints:
  - "Two-way sync" — "Changes flow in both directions" (default)
  - "One-way push" — "Local wins; the share mirrors this PC"
  - "One-way pull" — "Share wins; this PC mirrors it"
- "Realtime watcher" row: `--color-surface` panel, label + hint "Sync the moment a file changes",
  right-side custom switch — 40×22 pill, 1px border, knob 16px circle; ON: border
  `--color-accent`, track `color-mix(in srgb, #9184d9 30%, transparent)`, knob `--color-accent-200`
  (#e7e5fe), knob right; OFF: border `--color-divider`, transparent track, knob
  `--color-neutral-600` (#75798c), knob left. Default ON.
- `.btn.btn-ghost` "Advanced options" with `caret-right` → `caret-down` disclosure. Expanded panel
  (`--color-surface`): "Exclude patterns" (`*.tmp, ~$*, node_modules/`), 2-column
  "Upload limit" (`8 MB/s`) and "Keep file versions" (`5`), checkbox
  "Copy locked files via volume shadow copy".

**Footer row** (`margin-top: 22.4px`): monospace 12px muted summary on the left —
`Step N of 3` on steps 1–2, `<local>  ⇄  <share>` on step 3, truncated with ellipsis — then
`.btn.btn-secondary` "Cancel" (step 1) / "Back", then `.btn.btn-primary` "Continue" /
"Create pair" (step 3). Both buttons `flex: none; white-space: nowrap`.

On "Create pair": close the wizard, append the pair with status Syncing, detail
"First scan — comparing 1,204 files", and raise the creation toast.

### 3. Activity & history (`data-screen-label="Activity log"`)
**Purpose:** audit trail across all pairs; conflict review entry point.

**Layout:** 760px window, same chrome as the wizard (`list-magnifying-glass` icon, title
"DBsync — Activity", minimize + close buttons). Body `padding: 16.8px 22.4px 22.4px`.
Header row: `h4` "Activity & history" + 12px muted subline
"Last 24 hours across 4 folder pairs"; right-aligned tags "1,284 files sent" (`.tag-neutral`),
"312 received" (`.tag-neutral`), "2 conflicts" (`.tag-outline`).

`.table` columns: Time (74px, tabular numerals, muted), Event (110px — icon + label),
File (monospace 12px), Folder pair (150px, muted 12px), Result (96px, tag). Row rules fade to
transparent 48px from each end (built into `.table`); hover tint 4% text.

Event → icon / color: Sent `arrow-up` #d2cefd · Received `arrow-down` #cfd3e5 ·
Conflict `warning-diamond` #9184d9 (Result "Pending", `.tag-outline`) · Deleted `trash` muted ·
Retry `cloud-slash` muted (Result "Offline") · Locked `lock-simple` muted (Result "Skipped").

Footer: `.btn.btn-secondary` "Export log" (`export`) and `.btn.btn-ghost` "Review conflicts"
which opens the conflict dialog. Real app: filter by pair/date/event, virtualized rows, CSV export.

### 4. Conflict dialog (`data-screen-label="Conflict dialog"`)
**Purpose:** decide which copy wins when a file changed in both places.

**Layout:** modal over a dim backdrop (`color-mix(in srgb, #292b31 50%, transparent)`) covering the
whole app surface; card 520px, `.dialog` (background `--color-surface`, `--radius-lg`,
`--shadow-lg`, `padding: 11.2px`, gap 8.4px).

- Title row: `warning-diamond` (fill) 17px accent + "Both copies changed" (Inter 500, 20px).
- Body 14px at 85% opacity: "Q3-forecast.xlsx was edited on this PC and on `\\NAS-01\finance`
  since the last sync. Choose which copy wins." (share path in monospace).
- Two selectable cards in a 2-column grid, background `--color-bg`, `--radius-md`,
  `padding: 8.4px`, 1px border — `--color-accent` when selected, `--color-divider` otherwise:
  - "THIS PC" (10px, uppercase, 0.1em, accent) / "Today, 14:02" (14px) / "248 KB · dana" (12px muted)
  - "NETWORK SHARE" / "Today, 13:47" / "251 KB · m.reyes"
- Checkbox: "Do this for the other 1 conflict in this pair" (count is dynamic).
- `.dialog-actions`: `.btn.btn-secondary` "Keep both", `.btn.btn-primary` "Keep this PC" /
  "Keep network copy" (label follows the selected card).

"Keep both" renames the losing copy `Q3-forecast (dana, DESKTOP-7L).xlsx` — `<name> (<user>,
<machine>).<ext>` — and marks the pair In sync. Either action closes the dialog and raises a toast.

### 5. Toast notifications (`data-screen-label="Toast"`)
Windows-style notification, 348px, `--color-surface`, `--radius-lg`, `--shadow-lg`,
`padding: 11.2px`. Header line 11px at 55% opacity: accent `shuffle-simple` (fill) 13px + "DBsync"
+ 22×22 dismiss button. Title Inter 500 14px; body 12px muted. Auto-dismiss after 6s.
Stacked in a bottom-right flex column **above** the flyout with 8.4px gap (do not overlap the
flyout). In the shipped app these are real Windows toasts (`ToastNotificationManager`).

Exact copy:
| Trigger | Title | Body |
| --- | --- | --- |
| Pair created | Folder pair created | `<local>` is now syncing with `<share>` |
| Pause all | Syncing paused | DBsync will keep watching but send nothing until you resume. |
| Resume all | Syncing resumed | Catching up on 34 queued changes. |
| Conflict resolved | Conflict resolved | The copy on this PC / The network copy of Q3-forecast.xlsx was kept. |
| Keep both | Both copies kept | Saved as Q3-forecast (dana, DESKTOP-7L).xlsx alongside the original. |

## Interactions & Behavior
- **Tray icon** (taskbar, 30×30): click toggles the flyout. When open it gets a 1px accent border
  and a `color-mix(in srgb, #9184d9 14%, transparent)` fill. The glyph rotates while syncing.
  Real app: left-click opens the flyout, right-click opens a context menu (Open, Pause, Quit).
- **Pair row click:** Conflict → conflict dialog; any other status → activity window (real app:
  open that pair's detail/settings).
- **Pause all / Resume all:** flips every row to Paused with detail "Paused — resume to continue",
  freezes progress and the tray animation, and raises a toast.
- **Wizard:** Continue advances, Back returns, Cancel (step 1) closes; state persists across steps.
- **Progress:** the mock advances the syncing pair 1% every 700ms and wraps at 97% → 40%. Replace
  with real byte/file counts pushed from the service.
- **Stacking order:** flyout/toast column above windows in the mock (z-index 4), wizard 3,
  activity 2, conflict modal 5. Never let a window and the flyout overlap on open — the wizard is
  positioned clear of it (right: 424px).
- **Animations:** windows and toasts rise 10px with a fade over 120–160ms ease-out; progress sweep
  1.6s linear infinite; tray glyph 3.2s linear infinite.
- **States to design/build beyond this mock:** first-run setup, per-pair settings/delete,
  credential-failure re-prompt, disk-full, share permission denied, service-not-running banner.

## State Management
Tray-app state (mirrors service state; the service is the source of truth):
- `pairs[]`: `{ id, name, localPath, sharePath, destKind: 'unc'|'drive', direction:
  'two-way'|'push'|'pull', watcher: bool, status: 'syncing'|'in-sync'|'conflict'|'waiting'|
  'paused', pct, detail, excludes, uploadLimit, versionsKept, useVss, saveCredentials }`
- `pausedAll: bool` — global pause; overrides every row's displayed status.
- `appearance: 'light'|'dark'|'system'` (default `system`) + `systemIsLight: bool` read from the OS
  and kept live via a change subscription. Persist `appearance` per user.
- UI-only: `flyoutOpen`, `wizardOpen`, `wizardStep (1–3)`, `advancedOpen`, `logOpen`,
  `conflictOpen`, `conflictWinner: 'local'|'share'`, `applyToAllConflicts`, `toast`.
- Prototype tweaks that are **not** product features: `density` (comfortable/compact),
  `expertMode` (advanced options open by default), `openOnLoad` (which surface starts open).
- Push events needed from the service: pair status change, progress tick, conflict raised,
  share reachability change, log append.

## Theming — light, dark, and Match Windows
Three appearance modes: **Light**, **Dark**, and **Match Windows** (default), which follows the OS
setting live. In the prototype the root carries `data-theme="light|dark|system"`, the OS preference
is read with `matchMedia('(prefers-color-scheme: light)')` (re-read on `change`, no reload), and a
`is-light` class on the root applies the light ground when system mode resolves light. In the
Windows app, read `AppsUseLightTheme` under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize` (or WinUI
`Application.RequestedTheme` / `UISettings.ColorValuesChanged`) and re-theme without restarting.
The tray glyph must also swap to the light/dark variant so it stays visible on both taskbar grounds.

Only role tokens change between modes — every surface, border, shadow and text color reads from the
variables, so nothing else is duplicated. Dark is the Nocturne native ground; light re-derives the
same roles from the ramps (no pure white, ink-tinted elevation):

| Role | Dark | Light |
| --- | --- | --- |
| bg | #161826 | #e4e7f5 |
| surface | #232532 | #f3f5fe |
| text | #e9e9ed | #292b31 |
| accent | #9184d9 | #796cbf (accent-600) |
| accent text step (300) | #d2cefd | #5d5294 (accent-700) |
| accent-200 (toggle knob, progress sweep) | #e7e5fe | #423a6a |
| neutral-400 (calm status text) | #b2b6ca | #595d6c |
| divider | `color-mix(#e9e9ed 16%, transparent)` | `color-mix(#292b31 18%, transparent)` |
| shadow-sm | `0 0 0 1px #3f424d` | `0 0 0 1px #cfd3e5` |
| shadow-md | `0 0 0 1px #595d6c, 0 6px 18px rgba(0,0,0,.55)` | `0 0 0 1px #cfd3e5, 0 6px 18px rgba(41,43,49,.14)` |
| shadow-lg | `0 0 0 1px #9397ab, 0 16px 40px rgba(0,0,0,.65)` | `0 0 0 1px #b2b6ca, 0 16px 40px rgba(41,43,49,.18)` |
| progress track | #292b31 (neutral-900) | #cfd3e5 (neutral-300) |
| `.tag-neutral` | bg #3f424d / text #f3f5fe | bg #cfd3e5 / text #292b31 |
| `.tag-accent` | bg #423a6a / text #f5f4ff | bg #d2cefd / text #2b2741 |
| desktop wallpaper (prototype only) | `radial-gradient(120% 90% at 78% 12%, #1e2036, #161826 55%, #12131f)` | `radial-gradient(120% 90% at 78% 12%, #f3f5fe, #e4e7f5 55%, #d2d6e6)` |

Unchanged in both modes: type, spacing, radii, icon set, and the outlined-button treatment.
Muted text is always `color-mix(in srgb, var(--color-text) 55%/62%/70%, transparent)`, so it
follows the mode automatically.

## Design Tokens
From the Nocturne design system — `styles.css` is bundled; keep using the CSS variables rather than
hard-coded values where the target platform allows.

Dark-mode roles (see *Theming* above for the light column): bg #161826 · surface #232532 ·
text #e9e9ed · accent #9184d9 · divider `color-mix(in srgb, #e9e9ed 16%, transparent)`.
Neutral ramp 100→900: #f3f5fe #e4e7f5 #cfd3e5 #b2b6ca #9397ab #75798c #595d6c #3f424d #292b31.
Accent ramp 100→900: #f5f4ff #e7e5fe #d2cefd #b5abfc #968ae0 #796cbf #5d5294 #423a6a #2b2741.
Desktop background in the mock: `radial-gradient(120% 90% at 78% 12%, #1e2036, #161826 55%, #12131f)`.

Spacing (0.70× density): 2.8 · 5.6 · 8.4 · 11.2 · 16.8 · 22.4 px.
Radii: sm 4 · md 8 · lg 14 px.
Shadows: sm `0 0 0 1px #3f424d` · md `0 0 0 1px #595d6c, 0 6px 18px rgba(0,0,0,.55)` ·
lg `0 0 0 1px #9397ab, 0 16px 40px rgba(0,0,0,.65)`.

Type: Inter for headings and body. h4 20px/1.12, letter-spacing −0.015em, weight 500 (never
bolder). Body 15px/1.55 weight 400; UI labels 14px; secondary 12px; meta 11px; uppercase kickers
10–11px with 0.08–0.1em tracking. Paths use a monospace stack. Buttons are **outlined**, never
filled. Focus: `2px solid #9184d9`, offset 2px — never the browser default.

## Assets
- Icons: **Phosphor** (regular + fill), loaded in the prototype from
  `unpkg.com/@phosphor-icons/web@2.1.1`. Icons used: shuffle-simple, arrows-left-right,
  arrows-clockwise, check, check-circle, warning-diamond, cloud-slash, pause, play, plus, gear-six,
  clock-counter-clockwise, list-magnifying-glass, sun, moon, desktop-tower, folder, folder-open, network, hard-drive,
  hard-drives, monitor, arrow-up, arrow-down, trash, lock-simple, export, x, minus, caret-right,
  caret-down, caret-up, squares-four, magnifying-glass, wifi-high, speaker-high. Ship as vector
  assets (SVG/XAML paths), not a webfont.
- Font: Inter (Google Fonts in the prototype; bundle the font files in the app).
- No photography or raster assets.

## Files
- `DBsync.dc.html` — the full prototype (all five surfaces + the simulated desktop, both themes).
  Open in a browser; markup is at the top, behavior in the `class Component` script at the bottom.
  The theme variable overrides sit in the `<style>` block at the top of the template.
- `styles.css` — the Nocturne token sheet and component classes the prototype consumes.
