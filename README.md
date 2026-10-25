# Claudex

![Claudex: every limit, at a glance](docs/assets/banner.png)

> Personal fork of [Developer Island](https://github.com/maximilian467/Developer-Island) by Maximilian Köhlenbeck (MIT), renamed and extended by **soybean_alien**. Upstream is tracked as the `upstream` remote; see [CHANGELOG.md](CHANGELOG.md) for everything Claudex added.

> **Every AI limit, wallet and token, in one quiet capsule at the top of Windows.**

Claudex is a native, floating capsule at the top of your Windows screen. It shows how much of your **Claude**, **Codex** and **Hermes Agent** allowance is left, what each of them used today, what's playing, your focus timer, your next meeting and your machine's health at a glance, and opens into a full view when you click it.

[![CI](https://github.com/soybean-alien/Claudex/actions/workflows/ci.yml/badge.svg)](https://github.com/soybean-alien/Claudex/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/soybean-alien/Claudex?include_prereleases&label=release)](https://github.com/soybean-alien/Claudex/releases)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![Windows 10 and 11, x64](https://img.shields.io/badge/Windows-10%20%7C%2011%20x64-0078D4.svg)
![.NET 10, WinUI 3](https://img.shields.io/badge/.NET%2010-WinUI%203-512BD4.svg)

<p align="center">
  <img src="docs/assets/hero.gif" alt="Claudex in action (sample data): the notch, the compact capsule, a Claude Code live activity, Claude, Codex and Hermes usage with the Nous wallet and the usage graph, the usage strip, then Music, Focus, System and Awake" width="760">
</p>

<p align="center"><sub>Demo mode with invented data, rendered by the app itself (<code>--snapshot</code>). In the live app, the states morph with springs.</sub></p>

## Why Claudex?

AI coding agents spend tokens, plan limits and real credits where you can't see them, and the rest of a developer's day is scattered across a terminal, a media app, a calendar and Task Manager. macOS has the notch and the Dynamic Island idea; Windows has nothing in that spot. Claudex puts the few things you glance at all day into one small capsule that stays out of your way: native, local-first and cheap enough to run all day.

## Features

### AI usage: Claude, Codex and Hermes side by side

- **Claude Code and Codex** from their local logs: today's fresh tokens with the full breakdown (input, cache write, output, cache read, processed), sessions and model.
- **Plan usage, measured, never guessed:**
  - **Claude:** the 5-hour and weekly windows, read from claude.ai after a one-time sign-in in Settings (a few seconds after each reply, every 2 minutes otherwise). Terminal sessions can also hand them over through Claude Code's status line.
  - **Codex:** ChatGPT's shared Work/Codex allowance after signing in with ChatGPT (through the locally installed Codex), with every limit bucket and its reset time in Settings; Codex's own logs are the fallback.
- **Hermes Agent** ([Nous Research](https://github.com/NousResearch/hermes-agent)) in its own yellow column:
  - **The Nous wallet drains instead of filling:** full after a top-up or plan renewal, it empties as Hermes spends, turns amber below a threshold you pick (default $5) and red at $0 with a **Top up Nous wallet** button. Hover the amount for plan credits, purchased credits and the renewal date.
  - Read **every second while Hermes works**, every 5 minutes after 5 quiet minutes, and never while the PC is locked or asleep. Reading the balance is a free account read: it runs no model and spends no credits.
  - **Today on Hermes:** fresh tokens, input, cache write, output, cache read, reasoning, API calls, sessions and what Hermes spent (marked "estimated by Hermes" unless Nous billed an exact amount), plus the **model** Hermes used last, which can change at any time. From a Hermes install on this PC, or from your server through a small export job ([tools/hermes](tools/hermes)).
- **Running totals in gold:** this week, this month and lifetime, as an **estimated API equivalent** at public list prices for Claude and Codex. Hermes spends real credits, so its money stays in its own column and out of these totals.
- **A 26-week usage graph** of every tool's fresh tokens. Hover a day for each tool's share, click to pin it.
- **Usage by hour** over the last 28 days, with Anthropic's peak hours shaded (adjustable in Settings), your peak-hour share, and how fast Claude's 5-hour limit drains per dollar in and out of peak hours.
- **The usage strip:** with "Always display usage", the island rests as a slim strip with plan usage, the wallet, the gold totals and the graph, and opens in full while you use it.
- The compact capsule reads like `Claude 376k · 68%`, or Hermes's balance when you star Hermes.
- A short **live activity** when a Claude Code session starts, with the project's folder name.

### Media

- **Now playing** from any app that uses Windows media sessions (Spotify, Apple Music, browsers, Media Player): artwork, timeline, play and pause, previous and next, seeking where supported.

### Focus and productivity

- **Focus timer** with 25, 50 and 90 minute presets or a custom length; it survives restarts and keeps a daily history.
- **Calendar:** your next event and today's agenda from a private iCal link or an `.ics` file; within the hour before an event, the capsule shows it.
- **Tasks**, a small local to-do list, plus **Git** (branch, changes, push state of your current project) and **GitHub** (notifications, pull requests and CI through the GitHub CLI, read-only).
- **Favorites:** star a module and the compact capsule features it; several favorites take turns.

### Awake

- **Keep the PC awake, also with the lid closed**, so Claude Code, Codex and other agents keep working. Choose 1, 4 or 8 hours, a custom length, or Always (until you end it). The capsule shows the countdown with a coffee cup.
- During a session, the lid-close action of your power plan is set to "Do nothing" and put back afterwards; a value you change meanwhile stays yours. The change is written to a journal first, so a crash or restart cannot leave it behind. On battery, a session ends below 10 % (adjustable, or off). Quit ends the session. Use a well-ventilated surface, not a bag.

### System monitoring

- CPU, memory, GPU and battery with **60-second graphs** that turn orange or red under sustained load.
- **Temperatures and fan speeds** where the hardware exposes them (via LibreHardwareMonitor). Nothing is estimated: a sensor that isn't there is not shown.

### Home Cloud

- **Drop files onto the island** to copy them to a folder on your own server (for example `\\server\share\Inbox`). Off until you switch it on and set the folder in Settings, Home Cloud. It works in every state, also while the island is a notch or hidden: dragging a file toward the top edge arms a small drop zone. Uploads queue with progress; file names are never logged, and nothing goes to a cloud service.

### Smart island

- **Compact, live activity and expanded** states with interruptible spring animations. **Keep the island open** if you'd rather it didn't roll back up when you click elsewhere.
- **App-aware auto-hide:** while Chrome, Edge, Firefox or any app you list is in front, the island retracts into a 5 px notch in the screen edge (or hides).
- **Camera and microphone indicators** in every state, as tiny dots in the notch.
- **Anywhere you want it:** six anchors or a free position on any monitor, per-monitor DPI, a global shortcut (Ctrl + Alt + C), tray menu and Start with Windows.
- **Updates itself:** Settings, About checks this repository's releases through the GitHub CLI (daily if you like), and installs a new version on your click after checking its SHA-256.

All details per module: [docs/MODULES.md](docs/MODULES.md).

## Preview

| Claude, Codex and Hermes | The usage strip |
|---|---|
| <img src="docs/assets/usage-tooltip.png" alt="Expanded usage view (sample data): Claude and Codex plan usage, the Hermes column with the Nous wallet, the gold totals, the usage graph and a pinned day split by tool" width="400"> | <img src="docs/assets/usage-strip.png" alt="The usage strip (sample data): plan usage, the Nous wallet, the gold totals and the graph" width="400"> |
| **Music** | **System** |
| <img src="docs/assets/expanded-music.png" alt="Music: artwork, track, artist, timeline and controls" width="400"> | <img src="docs/assets/expanded-system.png" alt="System: CPU, memory and GPU graphs colored by load, battery, a storage temperature and a fan" width="400"> |
| **Compact capsule** | **Hermes and the Nous wallet, in Settings** |
| <img src="docs/assets/compact-featured-claude.png" alt="Compact capsule: focus countdown and Claude usage" width="400"><br><img src="docs/assets/compact-featured-hermes.png" alt="Compact capsule featuring the Hermes wallet (sample data)" width="400"><br><img src="docs/assets/notch.png" alt="The notch in the screen edge" width="100"> | <img src="docs/assets/settings-hermes.png" alt="Settings: the Nous Portal sign-in, where Hermes's token use comes from, and the low balance warning" width="400"> |

<sub>All screenshots show demo data.</sub>

## Privacy

Claudex is **local-first**. It has no account, no telemetry and no cloud sync, and it does not upload your prompts or code.

- **Read locally:** Claude Code transcripts (`~/.claude/projects`), Codex rollout files (`~/.codex/sessions`) and, for the Hermes column, Hermes's own database (read-only) or its export. Only usage metadata is extracted (token counts, costs, model, timestamps, session ids, working folder); prompt, message and code content is never stored, displayed or logged.
- **Stored locally** in `%LOCALAPPDATA%\Claudex` and `%USERPROFILE%\.claudex`: a SQLite database with token counts and daily totals, settings, tasks, focus history, the last plan percentages, the wallet bar's scale, and during an Awake session the lid setting to put back. Logs contain events and error types, never content or amounts, and are kept for 7 days.
- **Sign-ins stay on this PC and Claudex never sees your passwords:**
  - claude.ai: its cookies stay in a browser profile (`.claudex\claude-signin`); Claudex never reads the cookie values.
  - ChatGPT: handled by the locally installed Codex in an isolated profile (`.claudex\chatgpt-signin`); Claudex never reads those credentials.
  - Nous Portal: Nous's device sign-in (you approve a code on Nous's page). The tokens are encrypted for your Windows account (`.claudex\nous-signin.bin`), never logged, and used only to read the wallet. Nous lets them run models; Claudex never does.
  - Private calendar links are kept in Windows Credential Manager; GitHub goes through the GitHub CLI's own sign-in.
- **Network:** only what you switch on: claude.ai's usage page, Codex's ChatGPT requests, Nous Portal's account read, the copy of your server's Hermes export (over SSH with your own key), your calendar links, the GitHub CLI's read-only requests and the update check, and Home Cloud copies to your own server. None of these readings sends a message to an AI model.
- **Camera and microphone:** only "in use or not" is read from Windows' own consent store; app names are never stored or shown.

Full details: [docs/PRIVACY.md](docs/PRIVACY.md) and [SECURITY.md](SECURITY.md).

## Installation

1. Download `Claudex-Setup-<version>.exe` from the [latest release](https://github.com/soybean-alien/Claudex/releases/latest).
2. Run it. It installs per user into `%LOCALAPPDATA%\Programs`, without administrator rights and without a separate .NET runtime.
3. Launch **Claudex** from the Start menu. It appears at the top center of your screen.

Later versions install from inside the app (Settings, About, Updates). The installer is not code-signed yet, so Windows SmartScreen may ask you to confirm ("More info", then "Run anyway"). A portable zip is attached to each release as well: extract it completely and run `Claudex.exe`.

**Requirements:** Windows 11 x64 (tested). Windows 10 version 2004 or later is supported by the build but not yet tested. The Git module needs [Git for Windows](https://git-scm.com/download/win), the GitHub module and the updater the [GitHub CLI](https://cli.github.com/), ChatGPT plan usage the Codex app or CLI, and a server's Hermes export Windows' built-in OpenSSH client.

## Quick start

| Do this | And you get |
|---|---|
| Hover the capsule | A peek at the most relevant live information |
| Click it, or press **Ctrl + Alt + C** | The expanded island; Esc or a click elsewhere closes it |
| Click the ☆ next to a module's name | That module is featured in the compact capsule |
| Drag the capsule | Move it; it snaps to the nearest anchor |
| Settings, Modules, **Claude plan usage**, Sign in to claude.ai | Claude's 5-hour and weekly usage, a few seconds after each reply |
| Settings, Modules, **ChatGPT plan usage**, Sign in with ChatGPT | The shared Work/Codex allowance and every limit bucket, with reset times |
| Settings, Modules, **Hermes and the Nous wallet**, Sign in to Nous Portal | The Hermes column: your Nous wallet, Hermes's tokens and its model |
| Settings, General, **Always display usage** | The island rests as the usage strip |
| Settings, Position, **Auto-hide in apps** | Choose which apps make the island step aside |

Want to look around first? Start **Claudex (Demo)** from the Start menu, or run `Claudex.exe --demo`. Demo mode uses invented data only: it never reads your usage, wallet, tasks or repositories, and its settings are kept separately.

## Configuration

Everything is set in the Settings window and applies immediately:

| Section | Settings |
|---|---|
| General | Start with Windows, always on top, launch hidden, keep the island open, always display usage, keyboard shortcut, Claude peak hours |
| Position | Monitor, anchor, custom position; auto-hide in apps (notch or hide, only when maximized, app list) |
| Modules | Each module on or off and its order; Claude plan usage; ChatGPT plan usage; Hermes and the Nous wallet (sign-in, token source, low balance warning); Awake's battery floor; Git repositories; calendars |
| Home Cloud | Drop zone on or off, target folder, side, connection test |
| Appearance | System or Dark for Settings (the island itself is always dark) |
| About | Version, updates, daily update check, data folder |

API prices and the exchange rate for the API equivalent can be adjusted in `%USERPROFILE%\.claudex\pricing.json` ([example](docs/PRIVACY.md)).

## Architecture

```mermaid
flowchart LR
    subgraph Sources
        L["Claude Code and Codex logs"]
        A["claude.ai, ChatGPT, Nous Portal<br/>(plan and wallet readings)"]
        H["Hermes state.db or export"]
        M["Windows media sessions"]
        W["WinEvents, registry, sensors"]
        C["Calendars, git, gh"]
    end
    L & A & H & M & W & C --> P["Providers and readers<br/>(event-driven, scheduled)"]
    P --> S["State<br/>(Claudex.Core, tested)"]
    S --> V["View models"]
    V --> UI["WinUI 3 island<br/>(composition morph)"]
    S --> DB[("SQLite<br/>usage history")]
```

Data flows one way. `Claudex.Core` holds all logic without UI (the island state machine, auto-hide policy, parsers, aggregation, the Nous wallet and its polling rule) and is covered by unit tests; the WinUI app adds views, windowing and Windows integrations. A module that is turned off runs no watchers, timers or processes. Details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), [docs/PERFORMANCE.md](docs/PERFORMANCE.md).

## Tech stack

C# and .NET 10 · WinUI 3 and Windows App SDK 1.8 · WebView2 · SQLite (Microsoft.Data.Sqlite) · LibreHardwareMonitorLib · xUnit · Inno Setup

## Building from source

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and Git; Visual Studio is optional.

```powershell
git clone https://github.com/soybean-alien/Claudex.git
cd Claudex
dotnet test tests/Claudex.Tests     # build and run the tests
.\run.cmd -Demo                     # build and start with demo data (.\run.cmd for your real data)
```

Release build (tests, self-contained publish, portable zip, installer; the installer needs [Inno Setup 6](https://jrsoftware.org/isinfo.php)), then a check that nothing personal is in it:

```powershell
powershell -ExecutionPolicy Bypass -File tools/build-release.ps1
powershell -ExecutionPolicy Bypass -File tools/privacy-scan.ps1 artifacts/publish/win-x64 artifacts/Claudex-<version>-win-x64-portable.zip
```

More, including offscreen snapshots, the README media and the hardware report: [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) and [docs/RELEASING.md](docs/RELEASING.md).

## Accessibility

- Fully usable from the keyboard: the global shortcut opens the island with focus, Tab and the arrow keys move through it, Esc closes it and returns focus to where you were.
- Respects the Windows "Animation effects" setting.
- Per-monitor DPI aware: the island can be dragged between monitors with different scaling, and placement is recalculated when scaling or monitors change.
- Controls carry accessible names for screen readers; a full Narrator pass is still open.

## Known limitations

- **Plan usage needs a one-time sign-in** for Claude (claude.ai) and Codex (ChatGPT through the Codex app or CLI); token usage works either way. claude.ai's usage address isn't a public API: if it changes, Claudex shows "unknown format" instead of a wrong number.
- **Hermes:** the wallet works with Nous Portal only. Nous reports no model per call, so the model shown is the one in Hermes's own records (a minute late at most from a server). The Nous sign-in currently borrows Hermes's own app name, so Nous's page says "Connect Hermes".
- **Hardware sensors vary by device.** CPU temperatures need administrator rights on many machines, and many laptops expose no fan speed to applications; such readings are hidden, not estimated.
- The installer is **not code-signed** yet. Only **x64** is built and tested.
- More in [docs/STATUS.md](docs/STATUS.md).

## Roadmap

- A Claudex app name of its own with Nous Portal
- Code signing and a winget package
- Account-based calendars (Outlook, Google) through the calendar provider interface
- More AI tools through the provider interface
- A layout per display, and ARM64 builds

No dates; priorities follow feedback.

## Credits and AI disclosure
- Made by **soybean_alien**. Licensed under the **MIT License** (see [LICENSE](LICENSE)).
- Based on [Developer Island](https://github.com/maximilian467/Developer-Island) by Maximilian Köhlenbeck (MIT). Third-party components keep their own licenses: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
- Built on [.NET 10](https://github.com/dotnet/runtime) and [WinUI 3 / Windows App SDK](https://github.com/microsoft/WindowsAppSDK).
- Claude is a trademark of Anthropic, Codex and ChatGPT of OpenAI, and Hermes Agent and Nous Portal belong to Nous Research. Claudex is an unofficial project and isn't affiliated with or endorsed by any of them. Claude Code and Codex are shown with the Claude and OpenAI marks (from Simple Icons, CC0).
- **AI disclosure:** Claudex was designed with and written by **Claude Code (Anthropic, Claude Opus 5.5)**, with some releases by **Codex (OpenAI)**, directed and tested in the real app by the author on Windows 11.

The design follows an [Apple design analysis](docs/design/apple-design-analysis.md), the [Taste Skill](https://github.com/Leonxlnx/taste-skill) and the [Vercel Web Interface Guidelines](https://github.com/vercel-labs/web-interface-guidelines).
