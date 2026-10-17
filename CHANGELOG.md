# Changelog

All notable changes to Claudex are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

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
