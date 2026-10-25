# Security

## Supported versions

| Version | Supported |
|---|---|
| 0.1.x (latest release) | Yes |
| Older builds | No, please update |

## Reporting a vulnerability

Please do not open a public issue for security problems. Use GitHub's [private vulnerability reporting](https://github.com/soybean-alien/Claudex/security/advisories/new) instead. As this is an early-stage solo project, responses are best effort.

Never include secrets in a report or an issue: no calendar links, tokens, prompts, logs you have not read through, or private paths. If a secret is needed to reproduce a problem, describe it instead.

## Security model

Claudex is a local, single-user desktop app.

- It runs without administrator rights and opens no network ports.
- It makes network requests only for what you set up: claude.ai's usage answer while you're signed in for Claude plan usage, the calendar links you add (HTTPS or webcal), the Nous Portal account read while signed in for the Hermes column, and the GitHub CLI's read-only requests when the GitHub module is on. Links in the panels open in your browser.
- It reads local files of other tools (Claude Code transcripts, Codex rollout files) and extracts only usage metadata; it does not upload them.
- Private calendar links are stored in Windows Credential Manager for your user account, never in settings files or logs.
- "Claude plan usage" signs in to claude.ai on claude.ai's own page, in a WebView2 browser profile kept in `%USERPROFILE%\.claudex\claude-signin`. Claudex never sees the password, never reads or logs the cookie values or claude.ai's answers, and keeps only the two percentages and reset times. Sign out clears the profile.
- The Hermes column signs in to Nous Portal with Nous's device sign-in (you approve a code on Nous's own page). The tokens are encrypted with Windows' data protection for your account in `%USERPROFILE%\.claudex\nous-signin.bin`; Nous lets them run models, so Claudex uses them only for the account read that returns the wallet, never logs them and never sends them anywhere else. A server export of Hermes's usage is copied with Windows' OpenSSH, your own key and a host key you already trust (`StrictHostKeyChecking=yes`, no prompts).
- Its optional terminal status line adds Claudex as Claude Code's status line command in `~/.claude/settings.json` only when you click Connect, with a backup first, and never replaces a status line you already have. Disconnect removes it. The Claude Stop hook of versions before 0.5.14 is removed by the installer.
- The GitHub module uses the `gh` CLI and its sign-in; Claudex never sees or stores a GitHub token.
- Hardware sensors are read through LibreHardwareMonitorLib in read-only mode; fans are never controlled.

Reports about any of these boundaries (for example data leaving the machine, a secret appearing in logs, or settings of other tools being changed unexpectedly) are especially welcome.
