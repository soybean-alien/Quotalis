# Quotalis

Personal fork of [Developer Island](https://github.com/maximilian467/Developer-Island) by Maximilian Köhlenbeck. Private repo, not published. Called **Claudex** until 0.7.0. The working folder is `Desktop\Quotalis`; the old `Desktop\Claudex` copy is deprecated, so don't work in it.

## Rename compatibility (keep these Claudex literals)
- `NousTokenStore`'s DPAPI entropy stays `"Claudex Nous sign-in"`, or saved Nous sign-ins stop opening.
- `LegacyNames` (Core): old `--claudex-…` arguments still work, `MoveFolder` brings over `%LOCALAPPDATA%\Claudex` and `%USERPROFILE%\.claudex` at startup, `OldName` names the old Run entry and folders. `LegacyClaudeStopHook.Argument` stays `--claudex-stop-hook`.
- The installer keeps Claudex's AppId and removes `Programs\Claudex`. Releases no longer attach a `Claudex-Setup-X.Y.Z.exe` copy (dropped after 0.7.1, once no Claudex installs were left), so a Claudex older than 0.7.0 can't self-update; install Quotalis fresh instead.

## License and credit
- Quotalis is **MIT**, inherited from Developer Island. Do not switch it to GPL-3.0 (the default for soybean_alien's own projects).
- Never edit or remove Maximilian Köhlenbeck's copyright line in `LICENSE`.
- Keep crediting him in the README banner and Credits section, `THIRD_PARTY_NOTICES.md`, and the GitHub description/homepage.
- Credit the user as "Quotalis by soybean_alien", with no "Copyright ... soybean_alien" line.

## Workflow
- Work locally for the whole session; track revisions only in CHANGELOG `[Unreleased]`. No commits, tags or pushes until the user asks, then one commit, tag and push for the batch.
- Every change gets a `CHANGELOG.md` entry (Keep a Changelog), a SemVer bump in `Directory.Build.props` and a `vX.Y.Z` tag.
- Releases feed the in-app updater. After the batch is pushed and tagged: `tools/build-release.ps1`, then `tools/publish-release.ps1` (a GitHub Release on the private repo with the installer and `SHA256SUMS`). Publishing is part of the batch only when the user asks.
- The `upstream` remote has `tagOpt --no-tags`, because upstream's own `v0.x` tags collide with Quotalis versions. Merge fixes with `git fetch upstream; git merge upstream/main`.
- `CODEX_HANDOFF.md` (candid state for the next agent) and a short `AGENTS.md` pointing to it live in the root, gitignored. Keep the handoff current when work changes.
- Build and test: `run.cmd` (or `run.cmd -Demo`), `dotnet test tests/Quotalis.Tests`. Needs the .NET 10 SDK.
- The Claude status line argument is `--quotalis-statusline` (`--claudex-statusline` still works), which keeps it apart from Developer Island's `--claude-statusline`.
- UX: never show "~" before numbers; estimates are marked in words (e.g. "estimated" next to the reset time).
