# Changelog

All notable changes to Claudex are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

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
