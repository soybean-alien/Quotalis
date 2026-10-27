# Architecture

Quotalis is two projects plus tests:

| Project | Target | Contents |
|---|---|---|
| `Quotalis.Core` | `net10.0` | Everything that does not need Windows UI: models, parsers, providers, pricing, SQLite storage, settings, placement math, island state machine, module logic (Git, plan usage, Hermes and the Nous wallet, favorites), updates, demo data. Fully unit-tested. |
| `Quotalis` | `net10.0-windows10.0.22621.0`, WinUI 3 | The app: composition root, view models, views, motion, and platform integration (windowing, tray, autostart, monitors, camera and microphone, hotkey), the sign-in readers and the updater. |
| `Quotalis.Tests` | `net10.0`, xUnit v3 | 420 tests against Core, including a real `git` repository. |

## Data flow

```
 local logs / account reads / git           (background threads)
          │
   Provider  ──────────►  Snapshot (immutable record)
          │                      │
   ProviderHost (isolation)      │   marshalled to the UI thread by AppHost
          │                      ▼
   UsageHistoryService ──►  ViewModel (display strings, flags)
   (SQLite)                      │
                                 ▼
                           Views (x:Bind only, no parsing)
```

- **Providers never touch UI.** They publish immutable snapshots (`AiUsageSnapshot`); the Git module and the Hermes column hand finished state to their view models the same way.
- **View models format.** Numbers, currency, durations and dates are formatted via `DisplayFormat` (locale-aware, hyphen-only).
- **Views bind.** XAML uses compiled `x:Bind`; code-behind only handles input and motion.

## Providers

`LogFileUsageProvider` is the shared base of the Claude Code and Codex providers:

1. **Initial scan** on a background thread. Each file has a persisted cursor (`file_cursors`: length, write time, byte offset, parser state). Unchanged files are skipped and changed files are read from their offset, so a restart re-reads only what was appended. History is backfilled for 190 days.
2. **FileSystemWatcher**, debounced by 0.9 s, drives incremental reads. A watcher buffer overflow schedules one rescan. There is no polling.
3. **Two timers only.** One fires at local midnight (day rollover); the other clears the "active" flag once a tool has been quiet for 3 minutes. Both are one-shot.
4. **If the tool is not installed**, a watcher on the parent folder waits for its folder to appear.

`JsonlTailReader` reads complete lines only. A partially written trailing line stays for the next read.

| Source | Key | Notes |
|---|---|---|
| Claude Code transcript line | `message.id` + `requestId` | Claude Code writes one line per content block and repeats the usage, so events are de-duplicated. Only `usage`, `model`, IDs, timestamp and `cwd` are read. |
| Codex `token_usage_record` | `response_id` | Cached input is split out of input. |
| Codex `token_count` (older builds) | session + cumulative total | Repeated totals are skipped. `rate_limits.primary` provides the 5-hour window usage. |

`ProviderHost` owns the providers. An exception from a provider's start, dispose or snapshot becomes an `Error` snapshot plus a log entry, and never reaches the app. An exception from a UI handler is logged and swallowed.

## Storage

SQLite (`Microsoft.Data.Sqlite`) at `%LOCALAPPDATA%\Quotalis\usage.db`, WAL mode, versioned through `PRAGMA user_version` with append-only migrations.

| Table | Contents |
|---|---|
| `usage_events` | Provider, key, timestamp, local day, model, token counts, session, project. `INSERT OR IGNORE` de-duplicates. Pruned after 400 days. |
| `daily_usage` | Per day and provider: tokens, API value, partial flag, sessions. Kept indefinitely. |
| `focus_sessions` | Unused since 0.7 (Focus was removed); the table stays so the schema and migrations are untouched. |
| `file_cursors` | Read positions and parser state for incremental parsing. |
| `plan_usage_peaks` | Migration 2. The highest plan usage actually reported per day, provider and window. Never computed or backfilled. |

`UsageHistoryService` keeps running accumulators for the three most recent days. A write adds only the newly inserted events instead of re-reading the day. A randomized test checks that incremental totals equal a full recompute.

Settings are JSON (`settings.json`, source-generated serializer) and are written atomically: to a temp file, then renamed. A corrupt file is kept as `settings.corrupt.json` and defaults are used.

## Pricing

`ModelPricingService` holds public list prices in USD per million tokens (input, output, cache read, 5-minute and 1-hour cache writes). Estimates are shown in USD, the currency the list prices are published in.

- **Name matching.** A price matches an exact model ID or a dated snapshot of it (`claude-sonnet-4-20250514`). Newer models (`claude-opus-5-7`, `gpt-5.2`) stay unpriced instead of borrowing a guess.
- **Partial pricing.** Unpriced tokens mark the value as partial, per tool and day; the priced part is still shown and labelled "partly priced". `UsageHistoryService.Reprice` recomputes stored days from their events at startup, so new list prices apply to the past.
- **Overrides.** `pricing.json` in `%USERPROFILE%\.quotalis` can override prices and the rate.

## The island window

The hard parts of a Dynamic-Island-style window on Windows, and how they are solved:

| Problem | Solution |
|---|---|
| True per-pixel transparency in WinUI 3 | A custom `SystemBackdrop` that sets a fully transparent composition brush, plus the DWM sheet-of-glass setup (extend frame and an empty-region blur-behind). |
| Smooth morph without window-resize jank | The window has a **fixed size** (the largest capsule plus a 40 DIP margin). Only a composition shape animates. `IslandMorph` drives the rounded-rectangle geometry, the content clip and the shadow from one animated property set, using spring animations that can be retargeted mid-flight. |
| Transparent areas must not block clicks | `SetWindowRgn` follows the capsule. During a morph the region covers both shapes, and it shrinks when the springs settle. Outside the region, clicks reach the windows below. |
| Windows 11 border lines | The region stays 4 DIP inside the window edge. The active-window border is also suppressed on every activation. |
| Shadow | `LayerVisual`/`DropShadow` renders opaque on a transparent window, so a pre-rendered nine-grid texture (`Assets/island-shadow.png`) is stretched instead. It needs no offscreen pass per frame. |
| Focus stealing | `WS_EX_NOACTIVATE` means hover, click and drag never take focus. Expanding activates the window explicitly, and Esc returns focus to the previous window. |
| Staying on top | The window is always-on-top, but Windows orders that band by recency: another app's always-on-top window that comes to the front later covers it. On every foreground switch (`ForegroundWatcher.Switched`) the island takes the top of the band again with `SetWindowPos(HWND_TOPMOST, SWP_NOACTIVATE)`, which never activates it. |
| Closing | The island lives as long as the app: a close request (Alt+F4 while it is open) is cancelled and closes the island like Esc. Only Quit (tray or Settings) ends the app; `git` and `gh` run in a kill-on-close job object (`HelperProcessJob`), so a call still running ends with the app. |
| DPI and multi-monitor | Placement math is pure (`IslandPlacement`, `IslandDrag`) and works in physical pixels. The gap and offset use the monitor's scale, the window's content the DPI Windows renders it at; the two differ right after a move to another monitor. On `WM_DPICHANGED`, WinUI would apply Windows' suggestion (the old rectangle scaled), so `DpiChangeHook` subclasses the window and replaces it with the island's own rectangle for the new scale. A drag keeps the grabbed point in DIPs and converts it with the window's current DPI on every move; placement waits until the drop, and a short timer carries the drag on if the DPI change takes the pointer capture. The island's monitor (fullscreen, auto-hide) is the one under the capsule's anchor, never `MonitorFromWindow` on the much larger window. The monitor is saved by its device interface path, which survives Windows renumbering the `\\.\DISPLAYn` names (kept as a fallback for older settings); display change bursts are placed once they settle (400 ms). |
| Fullscreen apps | `FullscreenWatcher` combines a foreground WinEvent hook with the shell's `ABN_FULLSCREENAPP` notification. `FullscreenPolicy` (Core, tested) decides: maximized windows, captioned windows, our own windows and the shell's own surfaces never count. The Alt+Tab switcher and Task View cover the monitor without a caption; counted as fullscreen apps, they hid the island on every app switch. |

`IslandStateMachine` (Core, tested) decides the mode. Its inputs are named (`IslandInput`: PointerEntered, PointerExited, HoverDelayElapsed, Clicked, ClickedOutside, ActiveAppChanged, ModuleEvent, DragStarted, DragEnded) and documented in a transition table in the source.

- **Compact** at rest, or **Retracted** (the notch) or **Hidden** while an auto-hide app is in front.
- **Activity** (medium) on hover after a 280 ms dwell (450 ms on the notch), or for an event (default 4.5 s).
- **Expanded** on click or the global shortcut.
- **Hidden** from the tray or for a fullscreen app.
- **Dragging** is a flag of the machine: timers, events, hover and clicks wait until the drag ends.

Race safety:

- Every timer carries a generation number; a timer overtaken by a newer input never acts.
- When a timer fires, `PointerProbe` reports where the pointer really is, so a lost pointer-exit cannot keep an activity or peek open (an open peek re-checks every second).
- `HoverTracker` judges hover against the visible capsule and only counts movement as entering. A click counts only after the pointer moved onto the capsule, so a capsule that grows under a resting mouse cannot turn the next click into an expansion.
- While expanded, `OutsideClickWatcher` (a low-level mouse hook installed only in that state) closes the island on any press outside it, even if Windows refused it focus.

## Modules

Every module follows **Provider, then State, then ViewModel, then View**, with one panel per module and the shared states of `ModuleStatus` (disabled, loading, ready, empty, unavailable, error). `ModuleHost` owns the Git service and applies settings: a disabled module runs no watchers, timers or processes. Claude, Codex and Hermes are modules too (each can be switched off and starred), but they share the Usage tab and are fed by the providers, the plan readers and `HermesService`.

| Module | Source | Cadence |
|---|---|---|
| Git | `git` CLI for repositories added in Settings or seen in AI sessions (walks up from a session folder only) | Watches `.git` metadata; reads only while the panel is open, else on first sight; a branch switch is read from `.git/HEAD` without starting git |

## State, content and global signals

Four things are kept apart:

| Concern | Owner | Decides |
|---|---|---|
| Display state | `IslandStateMachine` | Hidden, Retracted, Compact, Activity, Expanded (plus the dragging flag) |
| Content | `CompactSelector`, `IslandContent` | Which module the capsule, the peek and the opened island show |
| Foreground app | `ForegroundWatcher`, `SmartHidePolicy` | The resting mode (Compact, Retracted or Hidden) |
| Privacy | `PrivacyWatcher`, `PrivacyIndicator` | Whether the camera and microphone marks show |

The display priority is Dragging > Auto-hide or hidden > explicitly expanded > hover peek > compact. An auto-hide app in front retracts the island at once from any state, even when expanded; a drag finishes first. Favorites and the last active module only choose content: `IslandContent.ChoosePeek` and `ChooseTabOnOpen` show the featured module (favorite, else the one opened last) on hover and on open, never a fixed default. The privacy marks are visible in every state except a hide the user asked for (tray, fullscreen): icons in compact, activity and expanded, 4 DIP dots in the notch (whose size never changes); with a device in use, `SmartHidePolicy` turns "hide" into the notch.

## Compact island and favorites

`CompactSelector` picks the featured module: one favorite is shown, several rotate every 7 s (skipping those with nothing to say), otherwise the module opened last (`ui-state.json`), otherwise the classic summary (Claude and Codex usage). The rotation timer runs only while the compact capsule is on screen. A featured Git repository adds its change count after a dot; Claude and Codex show no money in the capsule.

Ticking values sit in a `StableText`: an invisible copy of the widest form (`CompactWidth.Reserve`: "Claude 9%" reserves "Claude 000%", "376k" and "1.12M" reserve "000.00M") holds the slot and tabular figures make every digit equally wide, so the capsule changes width only when its content changes kind.

## Camera and microphone

Windows records each app's device use under `HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{microphone|webcam}` (packaged apps directly, desktop apps under `NonPackaged`): a `LastUsedTimeStart` without a `LastUsedTimeStop` means in use. `PrivacyWatcher` arms `RegNotifyChangeKeyValue` on both keys (subtree, names and values), reads them once, and waits; a change re-arms and re-reads after 120 ms. One background thread, no polling, no app names kept or logged. `ConsentStore` and `PrivacyIndicator` (Core) hold the testable rules.

## Plan usage

ChatGPT plan usage uses `CodexAccountClient` to speak the local Codex app-server JSON-RPC protocol through an isolated `CODEX_HOME` at `%USERPROFILE%\.quotalis\chatgpt-signin`. Codex owns the OAuth browser flow and credentials. `ChatGptUsageReader` checks sign-in and requests `account/rateLimits/read` every 2 minutes or shortly after Codex activity; errors back off to 5 minutes. `ChatGptPlanUsageParser` retains every returned limit bucket, its current and weekly percentages, and reset times, with a fallback for older single-bucket responses. Settings shows all buckets; `CodexUsageProvider` overlays the shared `codex` bucket on the local rollout limit, and returns to rollout data after sign-out. The account response and credentials are never logged.

Claude plan usage comes from claude.ai: `ClaudeWebReader` keeps a hidden WebView2 (parented to the host window, closed when the next read is over a minute away) in the browser profile `%USERPROFILE%\.quotalis\claude-signin`, where `ClaudeSignInWindow` shows claude.ai's own sign-in page and only checks that the session cookie exists. The reader opens `/api/organizations` (organization ids stay in memory, GUIDs only) and `/api/organizations/{id}/usage`, and `ClaudeWebUsage` turns `five_hour` and `seven_day` (`utilization`, `resets_at`) into `claude-plan.json`; answers are never logged. `ClaudeWebSchedule` reads a few seconds after the Claude provider sees a transcript change, at most every 15 seconds, every 2 minutes otherwise, and backs off after failures. A JSON 401 or 403 means the sign-in expired; anything else unknown shows nothing (`PlanConnection.SignInExpired`, `FormatChanged`). Terminal sessions can also hand over usage through Claude Code's documented status line input (`rate_limits.five_hour` and `seven_day`: `used_percentage`, `resets_at`). `ClaudeStatusLineSetup` adds Quotalis as the `statusLine` command in Claude Code's `settings.json` on request (backup first, never replacing a status line the user already has). `Quotalis.exe --quotalis-statusline` runs before any UI, keeps only those numbers in `claude-plan.json`, prints nothing and always exits 0. `PlanUsageService` follows that file with a watcher. Codex's 5-hour and weekly windows come from its own rollout records. A window that has reset no longer shows its percentage. `Quotalis.exe --quotalis-calibrate` (`PlanCalibration`) writes the same file from a reading entered by hand. The Claude Stop hook of versions before 0.5.14 is gone: `HookInstaller` removes its entries (`LegacyClaudeStopHook`), and `--quotalis-stop-hook` exits at once for an entry left elsewhere. `ClaudePlanLink.Combine` gives the island one state: a sign-in problem first, then either link counts as connected. `CodexStopHook` (`--quotalis-codex-stop-hook`) reads the latest `codex` rate-limit snapshot of the session it is called for and puts those numbers into the one usage line it asks Codex to add; it stores nothing but its trace. `PlanStatus` separates the link (connected or not) from the data (waiting, receiving, stale after an hour), and Settings and the Usage panel say which one applies.

## Hermes and the Nous wallet

`HermesService` feeds the Hermes column of the Usage tab from two sources. Token use comes from a local Hermes install's `state.db` (`HermesStateDb`, read-only) or from the metadata export a server writes once a minute (`hermes-usage.json`, copied with Windows' OpenSSH and cached at `%LOCALAPPDATA%\Quotalis\hermes-usage.json`); `HermesUsageParser` keeps numbers, model and surface names only, never message text. Hermes tokens feed the heatmap, but its costs (the ones Hermes recorded) stay in its column and never join the API-equivalent totals of Claude and Codex.

The wallet comes from Quotalis's own Nous Portal device-code sign-in (`NousPortalClient`: `/api/oauth/device/code`, `/api/oauth/token`, then `GET /api/oauth/account`), a free account read that never runs a model; Quotalis never calls an inference address. `NousPollSchedule` reads every second while Hermes works, every 5 minutes after 5 quiet minutes, never while the PC is locked or asleep (the host window's session-lock and suspend notifications), and backs off on failures up to 5 minutes, honouring Retry-After. `NousWalletParser` keeps the balance, plan name and period end only; `WalletGauge` keeps the bar's "full" scale since the last top-up in `hermes-wallet.json`.

## Auto-hide in apps

`AutoHideApps` (settings) lists apps with their own switches; the older `SmartHideProcesses` list is kept in sync for compatibility. `ForegroundWatcher` (WinEvent hooks, no polling) reports the foreground app; `SmartHidePolicy` decides Compact, Retracted or Hidden from the rules, the anchor (top center only) and, optionally, whether the window is maximized. `ForegroundWatcher` judges the root owner of the window actually in front (`GetForegroundWindow` when an event is processed), so a queued event from a fast switch or a browser popup cannot decide. `AutoHideCoordinator` (Core, tested) numbers every change: stepping aside applies at once, a return to the capsule waits 80 ms and is verified against the real foreground, and only the newest version may apply. A 750 ms check, running only while auto-hide can apply, re-reads the foreground and catches an event Windows never delivered.

## Secrets

`settings.json` holds no secrets. The Nous sign-in is encrypted with Windows' per-user data protection (DPAPI) in `%USERPROFILE%\.quotalis\nous-signin.bin` (`NousTokenStore`); Credential Manager is not used because Nous's tokens exceed its size limit. The rotated single-use refresh token is saved before it is used. The claude.ai sign-in lives in its WebView2 profile and the ChatGPT sign-in in Codex's isolated profile (see Plan usage). Tokens, answers and account details are never logged. Demo mode reads no account.

## Composition root

`AppHost` wires everything and is the only place that crosses threads (`DispatcherQueue.TryEnqueue`); `ModuleHost` does the same for the Git module. A single instance is enforced with a named mutex; a second launch signals a named event and exits. `--demo` swaps in an in-memory database, demo providers, demo plan readings, demo Git repositories and a made-up Hermes wallet and history, and uses its own settings file and instance name.

## Shared files (`%USERPROFILE%\.quotalis`)

Files that hooks, calibration and other programs write for Quotalis live in `%USERPROFILE%\.quotalis`, not in `%LOCALAPPDATA%\Quotalis`: `claude-plan.json`, `calibration-log.jsonl`, `stop-hook-last.json`, `codex-stop-hook-last.json` and `pricing.json`. Claude desktop is a packaged (MSIX) app, and Windows redirects new files that its hooks create under AppData into Claude's private package folder (`%LOCALAPPDATA%\Packages\Claude_…\LocalCache\Local\Quotalis`). A normally started Quotalis never sees those files. The user profile root is not redirected. At startup, `SharedFileAdoption` brings in the newest copy of each file from the old data folder and from any packaged app's private copy, merging the calibration log line by line. It only reads the sources.
