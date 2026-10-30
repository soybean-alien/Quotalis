# Quotalis for Linux

> A functional Linux version of Quotalis, which is a fork of [Developer Island](https://github.com/maximilian467/Developer-Island) by Maximilian Köhlenbeck.

Your AI usage limits for Claude Code, Codex and Hermes, in the terminal and in your bar. Built for Arch and Hyprland ([Omarchy](https://omarchy.org)): it joins Omarchy 4's own bar, or Waybar elsewhere.

**Status: tested on Omarchy 4.0.6 in a virtual machine, with made-up usage.** The test suite, install, service, bar module, click, screen keys and removal all ran there. Not yet tried: real Claude Code and Codex usage on Linux, the ChatGPT sign-in, Hermes, the Git page with real repositories, and the Waybar module on a real Waybar.

```
QUOTALIS (sample data)        Today $3.79
─────────────────────────────────────────
CLAUDE 5h ██████████░░░░  68%  2 h 14 min
       7d ██████░░░░░░░░  41%  Sat 09:00
CODEX  5h ████░░░░░░░░░░  31%  3 h 40 min
       7d ██░░░░░░░░░░░░  12%  Mon 14:30
HERMES today $1.27 estimated  week $3.11
```

There's no window of its own. Everything is text in the terminal's 16 colours and font, so it takes on your theme. When you change the Omarchy theme, Quotalis changes with it.

## Install

```bash
tar xzf quotalis-0.1.2-linux-x64.tar.gz
cd quotalis-0.1.2-linux-x64
./install.sh
```

Or on Arch, put the `PKGBUILD` next to the tarball, run `makepkg -si`, then `quotalis setup`.

`quotalis setup` does three things, and `quotalis setup --remove` undoes them:

1. Connects Claude Code's status line. If you already use another status line it's left alone, and Claude's plan bars stay empty.
2. Starts a systemd user service, which also starts at login.
3. Adds the bar module, keeping a dated backup of each file it changes:
   - **Omarchy 4 and later:** a command module at the start of `bar.layout.right` in `~/.config/omarchy/shell.json`. Move it to another spot in that list if you like; removal finds it wherever it is.
   - **Anywhere else:** a module in `~/.config/waybar/config.jsonc` and a few lines in `style.css`.

## Commands

| Command | What it does |
| --- | --- |
| `quotalis` | The screen: `g` git, `h` history, `u` usage, `q` quit |
| `quotalis print [usage\|git\|history]` | Print a page once |
| `quotalis signin chatgpt` | Read Codex's plan limits from your ChatGPT account |
| `quotalis service` | Run the service by hand |
| `quotalis bar` | One line for Omarchy's bar |
| `quotalis waybar` | The Waybar module's output |
| `quotalis config` | Show where settings and data are kept |

Add `--demo` to the screen, `print`, `bar` or `waybar` to see sample data.

## Where the numbers come from

- **Claude's plan bars:** Claude Code's status line, so they only move while Claude Code is running. There's no claude.ai sign-in on Linux.
- **Codex's plan bars:** Codex's session logs, or your ChatGPT account after `quotalis signin chatgpt`. That sign-in is held by Codex in a separate profile and doesn't change the account the Codex CLI uses.
- **Today's cost:** token counts in the local logs of Claude Code and Codex, at API prices. Hermes's cost is shown separately and never added to it.
- **Hermes:** `$HERMES_HOME/state.db` or `~/.hermes/state.db`. To read a server's export over OpenSSH instead, set `hermesEnabled`, `hermesSource`, `hermesRemoteTarget` and `hermesRemoteKeyPath` in `settings.json`.
- **Git:** repositories your AI sessions work in, read with `git`. The contributions graph needs the GitHub CLI.

The logs are read locally. The program's own requests are the ones `gh`, `codex` and `scp` make when those features are in use.

## Not in this version

- The Windows island, its panels and its animations.
- The claude.ai sign-in and the Nous wallet.
- The Codex Stop hook. It spends tokens on every turn, so it's left out on purpose.
- Self-updating. Install a newer tarball over the old one.

## Good to know

- The session folders under `~/.claude/projects` are watched. With many projects you may hit the system's inotify limit: raise `fs.inotify.max_user_watches` if readings stop moving.
- Setup doesn't touch your Hyprland config. The bar's click opens `xdg-terminal-exec --app-id=TUI.float quotalis`, or another terminal if that isn't installed. Omarchy floats that window by itself, and on Hyprland 0.55 or later Quotalis shrinks it to fit its text; elsewhere, add a window rule if you'd like it to float. Started by hand in a terminal, it never resizes that terminal.
- The time after each percent is when that limit resets.
- Omarchy 4 has its own `omarchy.agents` bar widget for Claude and Codex limits. Quotalis adds the limits at a glance on the bar, cost at API prices, Hermes, the Git page and the usage history. You can keep both, or drop Omarchy's with `omarchy plugin disable omarchy.agents`.
- On Omarchy's bar the module turns on the accent colour once a limit reaches 80%. The tools take turns every 7 seconds, and the tooltip shows them all. Each turn runs the program once, about 0.11 s of CPU. Waybar's module shows them side by side instead.

## Build and test

Needs the .NET 10 SDK.

```bash
dotnet test tests/Quotalis.Tests
```

```bash
./packaging/build.sh
```

On Windows, real data needs `QUOTALIS_HOME` set so a development run can't touch an installed Windows Quotalis. `--demo` needs nothing.

## Credits and AI disclosure
- Quotalis by soybean_alien. Licensed under the **MIT License** (see [LICENSE](LICENSE)), inherited from Developer Island.
- [Developer Island](https://github.com/maximilian467/Developer-Island) by Maximilian Köhlenbeck, the project this is forked from. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
- Quotalis is an unofficial tool and isn't affiliated with Anthropic, OpenAI or Nous Research.
- **AI disclosure:** Quotalis for Linux was designed with and written by **Claude Code (Anthropic, Claude Opus 5.5)**, directed by the author. It was tested on Omarchy 4.0.6 in a virtual machine with made-up usage, not yet with real Claude Code or Codex usage on Linux.
