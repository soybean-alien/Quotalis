# Changelog

All notable changes to Claudex are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.5.12] - 2026-10-19

### Fixed

- **Starting Claudex while it was already running did nothing you could see.** A second start (a desktop, Start menu or pinned taskbar shortcut, or a Stream Deck launcher key) only re-showed the resting island. It now opens the island in front, as clicking the tray icon does, and logs "Second start".

## [0.5.11] - 2026-10-19

### Added

- **Codex's 5-hour and weekly limits are published for other programs**, the way Claude's already are. The Claudex Mini Stream Deck plugin reads them, so its Codex keys show the same numbers as the island. Both files are in `%USERPROFILE%\.claudex`:
  - `codex-plan.json`: the newest 5-hour and weekly readings (% used, reset time, window length), written atomically whenever Codex reports new numbers. Values are as Codex reported them; a window whose reset time has passed counts as 0% used, as on the island.
  - `codex-limit-log.jsonl`: one line per reading, so usage per day can be worked out. Lines older than 35 days are dropped.
  - Numbers only: no prompts, paths or account details.

## [0.5.10] - 2026-10-19

### Fixed

- **Claude's plan bar and 5H / Weekly switch were missing whenever Claudex was started normally.** Claude desktop is a packaged (MSIX) app, and Windows redirects new files its hooks create under AppData into Claude's private package folder (`%LOCALAPPDATA%\Packages\Claude_…\LocalCache\Local\Claudex`). Every calibration from the Stop hook landed there, so Claudex started from Explorer, the Start menu or autostart never saw a reading. Only a copy started from inside a Claude session did, which is why a restart sometimes "fixed" it. The same redirection hid `pricing.json` from a normal start, so `codex-auto-review` went unpriced and history was re-priced back and forth at each start.
  - Files that hooks and other programs write for Claudex now live in **`%USERPROFILE%\.claudex`**, which Windows does not redirect: `claude-plan.json`, `calibration-log.jsonl`, `stop-hook-last.json`, `codex-stop-hook-last.json` and `pricing.json`.
  - At startup, Claudex brings in the newest copy of each from the old folder and from any packaged app's private copy, and merges the calibration log line by line. It only reads the old copies and never deletes them.

## [0.5.9] - 2026-10-18

### Fixed

- **The 5H / Weekly switch disappeared after a reset.** A window that had reset was dropped, and the switch only showed while both windows were live. After Codex's off-schedule weekly reset its old 5-hour window ran out, so Codex showed Weekly alone. A window that has reset now shows as 0%, marked "Reset, starts on next request", and the switch stays for Claude and Codex. Readings are still merged newest first, so an early reset is followed as soon as it is logged.
- **Claude's plan bar could stay "Unavailable" while readings arrived.** The island kept the reading it found at startup and only learned of new ones from file events; one running copy missed every update until it was restarted. Opening the Usage panel now re-reads `claude-plan.json`, a failed read after a file event is retried once, a failed file watcher is restarted, and read failures are logged as warnings (they were debug-only), with what was found at startup.
- **Quitting from Settings logged two errors.** Settings closed after the island and tried to change the island's always-on-top state on a window that was already gone.

### Added

- `docs/assets/heatmap-demo.gif`: the usage graph being hovered, clicked to pin a day, and left with the pin kept (demo data). The snapshot run renders the walk (`80-walk-*`), and `tools/make-heatmap-gif.py` adds the cursor and captions.

## [0.5.8] - 2026-10-17

### Fixed

- **Settings opened behind the island.** The island re-claims the top of the always-on-top layer whenever another window comes to the front, and Settings counted as one. While Settings is the active window, the island now leaves that layer, and it returns as soon as you switch away or close Settings.
- **The open island could not be moved.** Dragging worked only in the pill, peek and notch, so with "Keep the island open" there was no way to move it. The open island now drags from any part of the capsule its controls leave free.

## [0.5.7] - 2026-10-17

### Added

- **Usage by hour and Claude's peak hours.** Below the usage graph, while no day is inspected or pinned:
  - **Hour strip:** 24 bars show when you use Claude and Codex (estimated API equivalent by local hour, last 28 days), with Anthropic's peak hours shaded bright yellow. Hovering a bar shows that hour's fresh tokens and cost.
  - **Peak share:** one line gives the share of your usage that falls in peak hours.
  - **Limit drain:** another compares how fast Claude's 5-hour limit fills per $1 of usage in peak versus off-peak hours, measured from your own exact readings. It pairs consecutive readings in the same 5-hour window, at most two hours apart, against the Claude usage logged between them, and shows the result once each side has 5 intervals and $1 of usage; until then it counts the readings so far. Readings come from Claudex's calibration log and, when it is still on this PC, Tokenz's older log (read only).
- **Codex history from your ChatGPT profile.** `Claudex.exe --claudex-import-codex-profile <profile.json>` imports the "Token activity" of a ChatGPT profile (the JSON behind `chatgpt.com/u/<name>`), so the usage graph covers Codex days from before Claudex tracked them.
  - **Which days:** only days before the first Codex day Claudex measured itself. The profile counts UTC days, so later days would overlap measured evenings and count them twice.
  - **Estimates:** the profile gives one total per day, so each total is split by your measured Codex mix (input, output, cached input) and valued at your measured cost per million tokens.
  - **Labels:** the day readout says "estimated" and "Codex from your ChatGPT profile", and the gold totals note that they include imported days.
  - **Provenance:** imported days are recorded in a new `imported_days` table. A later local measurement of the same day replaces the import, and re-pricing never touches imported days.
- **Settings, General, Keep the island open.** When on, the open island stays open when you click elsewhere or switch windows, instead of rolling back into the pill. It opens as soon as you turn the option on and when Claudex starts. Esc and the keyboard shortcut still close it, and full-screen and auto-hide apps still move it aside.
- **Settings, General, Claude peak hours:** start and end hour, weekdays in US Eastern time. The default is 8 AM to 2 PM, as Anthropic announced in March 2026; it can't be confirmed for later dates, hence the setting.

### Changed

- **The hours block and the day readout show together.** Inspecting or pinning a day no longer swaps the hour strip out: the day's numbers appear in their own area below it (a short hint stands there otherwise), so the layout never jumps. The open island grows at the bottom to fit both (up to 920 DIP, from 780).

### Fixed

- **Flaky GitHub module test.** `Without_gh_the_module_explains_how_to_connect` sometimes saw `Loading` instead of `Unavailable`: the module's first refresh ran on a real timer, held the fetch lock, and the test's own refresh returned before the status changed. The test now uses a fake clock, so that first refresh runs inside `Configure`. Test-only; the app is unchanged.

## [0.5.6] - 2026-10-17

### Added

- **Pin a day in the usage graph.** Left-click (or tap) a day, and its readout below the graph stays after the pointer leaves, marked "· pinned" next to the date, so you can copy the numbers down. Hovering other days still previews them, a fainter ring keeps marking the pinned day, and leaving the graph goes back to it. Clicking another day moves the pin; the pin clears when the island rolls back into the pill. Enter or Space pins the day chosen with the arrow keys. The pin follows the date, so live updates and midnight don't move it.

## [0.5.5] - 2026-10-17

### Changed

- **No "Cache write" row for Codex.** Codex's logs carry a `cache_write_input_tokens` field, and Claudex reads it, but it is always 0 (all 10,005 records on the development PC). OpenAI caches prompts automatically and has no separate cache-write charge, so a permanent "0" only looked like a bug. Claude keeps the row (its cache writes are real and priced at 1.25× or 2× input). Codex's cached input still shows as "Cache read".

### Fixed

- `tools/publish-release.ps1` works in Windows PowerShell 5.1: its `gh` probes ("is the tag pushed?", "does the release exist?") no longer stop the script when `gh` writes to stderr.

## [0.5.4] - 2026-10-17

### Added

- **Self-updater** in Settings, About, Updates. Claudex is never public, so it updates from the private `soybean-alien/Claudex` releases through the GitHub CLI you are signed in to (`gh`); Claudex never sees or stores a token.
  - **Check:** asks for the latest release and compares it with this build. "Check for updates daily" (on by default) repeats the check once a day, starting two minutes after launch.
  - **Install:** when a newer release exists, the button becomes "Update to vX.Y.Z". Confirming downloads `Claudex-Setup-X.Y.Z.exe`, checks its SHA-256 against GitHub's digest or the release's `SHA256SUMS-X.Y.Z.txt` (it refuses to install without a match), starts the installer silently and closes Claudex. The installer updates in place, keeping usage history, settings, hooks and autostart, and starts the new version.
  - A development build checks but never installs. Demo mode doesn't check.
- `tools/publish-release.ps1` publishes a built release (installer, portable zip, `SHA256SUMS`, with this version's changelog section as notes) to the private repository, after the tag is pushed. Installed copies then find it.

### Changed

- Settings, About: the "Local-first" text names the update check as Claudex's only network request of its own, and the "API equivalent" text says US dollars (it still said euros).

## [0.5.3] - 2026-10-17

### Added

- **List prices for the current Codex models**, from OpenAI's pricing page: GPT-6.1 Sol ($2 / $10, cached input $0.10), GPT-6 Sol ($2 / $10, $0.20), GPT-6 Luna ($0.10 / $0.50, $0.01), GPT-5.6 Sol ($4 / $20, $0.40, promotional until at least November 21, 2026), GPT-5.6 Terra ($2 / $12, $0.20), GPT-5.6 Luna ($0.20 / $1.20, $0.02) and GPT-5.3 Codex ($1.75 / $14, $0.175), standard tier for prompts up to 272K tokens. Codex days now show their estimated API cost. `codex-auto-review` has no published price and stays unpriced.
- **Running cost totals.** Above the usage graph, three gold figures show the estimated API equivalent of Claude and Codex together: **This week** (from the first day of the week the graph uses), **This month** (calendar month) and **Lifetime** (every stored day). They are summed from the same daily values as the rest of the panel and refresh with every new usage, like the other figures. A line says "Partly priced" when some tokens have no list price. The open island is 80 DIP taller (up to 780) to make room.
- **Past days are re-priced at startup.** After the first log scan, every stored day is recomputed from its events with the current prices, so days recorded before a model had a list price show their cost. A day whose events are no longer all stored (pruned, or a database rebuilt from logs that are gone) keeps its stored total instead of shrinking.

### Changed

- **No scrollbar on the open island.** The panel area and the Tasks list no longer show a scrollbar; it appeared on hover and covered numbers at the right edge. The mouse wheel and touch still scroll. Settings keeps its standard bar.
- **Partly priced days show their cost.** The usage graph readout used to say "Unavailable" for any day with one unpriced model, hiding even a fully priced Claude cost. "Partly priced" is now tracked per tool: each tool shows its priced cost, a tool with unpriced models adds ", partly priced", one with none priced says "no list price", and the total reads, for example, "$4.39, partly priced".

### Fixed

- `docs/DEVELOPMENT.md` shows `.\run.cmd` and `C:\temp\shots` again; the fork's rename had turned `\r` and `\t` in those paths into control characters.
un.cmd` and `C:	emp\shots` again; the fork's rename had turned `
` and `	` in those paths into control characters.

## [0.5.2] - 2026-10-17

### Changed

- **New banner and social preview.** A black, Apple-style campaign banner ("Every limit. At a glance.") with the Claudex icon, the real compact capsule and expanded usage view from demo mode, the Developer Island credit and the soybean_alien emblem. `tools/banner/render.ps1` renders `docs/assets/banner.png` (2560 x 1280) and `social-preview.png` (1280 x 640) from `tools/banner/banner.html` with headless Chrome or Edge.
- **New README animation.** `hero.gif` is 1270 x 840 on a black stage, about 22 s: the notch, the capsule, a Claude Code activity, Claude and Codex usage with the purple graph and the orange and blue day bars, Music, Focus, System and Awake, each with a one-line caption. Its palette is taken from every state, so the provider colors survive. The screenshots are re-rendered from the current app; Calendar and Git keep their earlier ones.

## [0.5.1] - 2026-10-17

### Fixed

- **Claudex could freeze right after a reboot** (no island, a tray icon that did nothing). The first scan of the Claude logs turned every line, including huge tool outputs, into a string. That forced full garbage collections while WinUI was still building the island, and the two deadlocked: the collection waited on WinUI's reference tracker, and the UI thread waited on the collection. The scan now starts once the island has rendered and the UI thread is idle, and lines without `"usage"` and `"assistant"` are skipped before they are decoded, so a full scan barely touches the large object heap.

## [0.5.0] - 2026-10-17

### Added

- **Claude desktop calibration moved from Tokenz into Claudex.** `Claudex.exe --claudex-calibrate --five-hour X --five-hour-reset T --weekly Y --weekly-reset T [--plan P] [--by NAME] [--source TEXT]` saves exact plan usage from the Claude desktop app's usage card into Claudex's own plan file and prints "applied by Claudex". Reset times take ISO 8601 or Unix seconds; `--` marks a missing window.
- **Stop hook** `Claudex.exe --claudex-stop-hook`, the same rule Tokenz used: in a Claude desktop session over 2 minutes old it asks Claude to read `get_usage`, run the calibration and end with `🟠 Usage: Claude 5h <x>% · wk <y>%`. It skips a turn when a reading arrived in the last 30 seconds. Readings go to `%LOCALAPPDATA%\Claudex\calibration-log.jsonl`, the last decision to `stop-hook-last.json`.
- **Codex Stop hook moved from Tokenz into Claudex.** `Claudex.exe --claudex-codex-stop-hook` (in `~/.codex/hooks.json`), in Codex sessions over 2 minutes old, reads that session's latest exact `codex` rate-limit snapshot from its rollout log and asks Codex to end its reply with `🔵 Usage: Codex 5h <x>% · wk <y>%`, numbers already filled in, so Codex runs nothing for it. A window that has reset, or a session without a snapshot yet, shows `--`. It skips a turn within 30 seconds of the last report, and tells Codex to pass Claude numbers you give it from claude.ai to `--claudex-calibrate`. Its last decision goes to `%LOCALAPPDATA%\Claudex\codex-stop-hook-last.json`.
- **Installer hooks.** `Claudex.exe --claudex-hooks connect` points the Claude Code status line and Stop hook, and the Codex Stop hook, at that copy of Claudex (moving them from wherever an earlier copy put them, adding missing ones, leaving your own status line alone). The installer runs it as a task, "Connect Claude Code and Codex usage", on by default. `--claudex-hooks disconnect` removes only the entries that run that copy, and the uninstaller runs it first, so Claude Code and Codex never call a missing program.
- The installer offers a **desktop shortcut** and shows the new icon in its wizard.
- Settings, Modules, "Connect" adds the Stop hook next to the status line (and adds only the hook when you already have your own status line). "Disconnect" removes both, and nothing else.

### Removed

- **Tokenz is no longer read.** The `tokenz_status.json` reader, the "Reading from Tokenz" Settings text and Tokenz's stale and waiting messages are gone. Plan bars come only from Claudex's status line and its Stop hook.

### Changed

- **New icon.** A black rounded square with the Claude mark's left half in Claude orange and the OpenAI mark's right half in Codex blue, ringed by an orange and a blue arc. It is the app, tray, taskbar, desktop shortcut and installer icon. `tools/make-icon.ps1` draws every size from vectors (thicker arcs at 32 px and below) and also writes the installer's wizard images.
- **Provider colors.** The Claude logo is drawn in Claude orange and the Codex logo in Codex blue (transparent marks, not app icons) everywhere they appear: the resting island, peeks, the Usage tab icon and the panel headers.
- Each provider's plan bar and live-session dot use its color, and the usage readout bars use the same theme colors.
- The classic resting state shows each tool's logo before its name.


## [0.4.0] - 2026-10-17

### Changed

- **Usage graph redesign.** The heatmap pools Claude and Codex and is shaded in purple: dim, brighter and brightest for the lighter, middle and heaviest third of your active days.
- Inspecting a day shows its combined fresh tokens, then an orange Claude bar above a blue Codex bar (each with its tokens and cost, on one scale), the session count and the day's total cost in USD.
- The floating tooltip over the graph is gone; the readout below it shows everything.
- The footnote is one line: "Costs are API list prices, not what you pay."


## [0.3.1] - 2026-10-17

### Changed

- Plan percentages never show a `~`. An estimate between exact readings says "estimated" next to its reset time instead.


## [0.3.0] - 2026-10-17

### Added

- **Claude plan bars from Tokenz.** When Tokenz is installed, Claudex reads its `%LOCALAPPDATA%\Tokenz\tokenz_status.json` (Claude `five_hour` and `weekly` only) and shows the 5-hour and weekly bars like Codex's. This works with the Claude desktop app, which never runs Claude Code's status line. Estimates between Tokenz's exact readings show as `~23%` and "estimated" in the reset line. They are never stored as measured daily peaks. Without Tokenz, the status line bridge is used as before.
- Settings, Modules shows "Reading from Tokenz" and when it last updated.

## [0.2.0] - 2026-10-17

### Changed

- The default global shortcut is **Ctrl + Alt + C**. Ctrl + Alt + Space was already taken by another app on this PC; it stays available as a preset in Settings.
- The estimated API equivalent is shown in **US dollars**, the currency the list prices are published in. The EUR conversion and the `usdToEur` override in `pricing.json` are gone.
- Git and Calendar are off by default until they are set up (turn them on in Settings, Modules).

### Added

- List prices for Claude Sonnet 5.5 ($2 / $10, cache reads $0.20) and Claude Haiku 5.5 ($0.10 / $0.50 for prompts up to 100K tokens), so their usage is no longer "partly priced".

## [0.1.0] - 2026-10-17

Forked from Developer Island 0.3.0 (upstream commit `ada8ef6`).

### Changed

- Renamed to **Claudex**: namespaces, projects, solution, executable, tray, Settings and installer.
- Own identity, so Claudex runs side by side with Developer Island: data folder `%LOCALAPPDATA%\Claudex`, single-instance mutex, autostart entry, Awake restore task and a new installer AppId.
- The Claude status line bridge uses its own argument, `--claudex-statusline`, so a status line owned by Developer Island counts as someone else's and is never reported as connected or removed.
- Version restarts at 0.1.0.

---

Developer Island history (upstream, before the fork):

## [0.3.0] - 2026-10-15

### Added

- **Awake** module (a coffee cup): keeps the PC awake for 1, 4 or 8 hours, a custom length or Always, also with the lid closed, so coding agents keep working. During a session the lid-close action is "Do nothing"; it is journaled first and put back at the end, at the next start or by a one-time scheduled task if the app is gone. On battery, a session ends below 10 % (adjustable). Countdown in the capsule, Stay Awake in the tray menu.
- With nine tabs, the tab buttons of the open island are 28 instead of 32 DIP wide, so the section name still fits.

## [0.2.0] - 2026-10-13

### Added

- **Home Cloud drop zone:** drop files or folders onto the island to copy them to your own server. It is off until you set a target folder. It works in every state, also while the island is a notch or hidden: dragging toward the top edge arms the zone. Progress, success and error feedback; uploads go through a `.part` file and a rename. File names and paths are never logged. Settings, Home Cloud sets the target and the side, and turns it all off.
- `--homecloud-upload=<path>` sends a file through the Home Cloud pipeline for testing.
- **Quit Developer Island** in Settings, General: ends the whole app (island, tray icon, background work) after a confirmation. Closing Settings or hiding the island keeps it running.

### Fixed

- The island no longer disappears while switching apps: the Alt+Tab switcher and Task View were taken for fullscreen apps and hid it on every switch.
- The island stays above other apps' always-on-top windows: it takes the top again whenever another app comes to the front, without taking focus.
- Alt+F4 on the open island closes it like Esc instead of removing it until the next start.
- The usage graph reacts to hover, click and tap again; pointer input went to the scroll area behind it.
- The favorite star sits in the same place on every tab, Usage (Claude Code) included, and is no longer pushed aside or cut off by long names. On Usage it stands for the enabled providers.
- Git and GitHub CLI calls still running when the app quits end with it.
- The island can be dragged onto a monitor with a different scale. It used to jump back to its old monitor, and the capsule slid away from the cursor; now the point you hold stays under it, and a DPI change cannot end the drag.
- After moving to a monitor with another scale, the island lands at its anchor at once instead of briefly at the wrong size and place.
- The island remembers its monitor by hardware id, so it returns to the right display after docking or reconnecting, when Windows renumbers the displays.
- Fullscreen apps and auto-hide apps count only on the island's own monitor, also when its window reaches into the neighbouring one.
- Connecting or disconnecting displays places the island once the change has settled, and the Monitor list in Settings updates.

### Security

- Git status ignores a repository's own `core.fsmonitor` setting, so a prepared repository in an AI session's folder cannot start a program through the Git module.
- Calendar downloads are capped at 16 MB also when the server sends no size.

## [0.1.0] - 2026-10-01

First public version.

### Added

- **The island:** a native WinUI 3 capsule with compact, live activity and expanded states, spring morphs from its anchor, six anchors or a free position on any monitor, a global shortcut (Ctrl + Alt + Space), tray menu and Start with Windows.
- **Claude Code and Codex usage** from their local logs: fresh and processed tokens by category, sessions, a 26-week usage graph with day tooltips, and an estimated API equivalent at list prices.
- **Plan usage:** Claude's 5-hour and weekly windows through Claude Code's status line (terminal sessions), Codex's windows from its own logs.
- **Music** for any app using Windows media sessions, with artwork, timeline and controls.
- **Focus** timer with presets, restore after restart and daily history.
- **Calendar** from private iCal links (stored in Windows Credential Manager) or `.ics` files.
- **System** monitoring: CPU, memory, GPU and battery with 60-second graphs colored by load, plus the temperatures and fans the device exposes.
- **Git**, **GitHub** (through the GitHub CLI, read-only) and **Tasks** modules.
- **Favorites:** star modules to feature them in the compact island; otherwise the module opened last is shown.
- **Auto-hide in apps:** retracts into a small notch (or hides) while a listed app such as Chrome is in front, deterministic under fast app switching.
- **Camera and microphone indicators** in every state, as small dots in the notch.
- **Demo mode** (`--demo`) with invented data, offscreen snapshots (`--snapshot`) and a hardware report (`--system-report`).
- Per-user installer and portable zip for Windows x64.
