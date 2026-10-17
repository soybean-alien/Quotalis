# Claudex

Personal fork of [Developer Island](https://github.com/maximilian467/Developer-Island) by Maximilian Köhlenbeck. Private repo, not published.

## License and credit
- Claudex is **MIT**, inherited from Developer Island. Do not switch it to GPL-3.0 (the default for soybean_alien's own projects).
- Never edit or remove Maximilian Köhlenbeck's copyright line in `LICENSE`.
- Keep crediting him in the README banner and Credits section, `THIRD_PARTY_NOTICES.md`, and the GitHub description/homepage.
- Credit the user as "Claudex by soybean_alien", with no "Copyright ... soybean_alien" line.

## Workflow
- Work locally for the whole session; track revisions only in CHANGELOG `[Unreleased]`. No commits, tags or pushes until the user asks, then one commit, tag and push for the batch.
- Every change gets a `CHANGELOG.md` entry (Keep a Changelog), a SemVer bump in `Directory.Build.props` and a `vX.Y.Z` tag.
- Releases feed the in-app updater. After the batch is pushed and tagged: `tools/build-release.ps1`, then `tools/publish-release.ps1` (a GitHub Release on the private repo with the installer and `SHA256SUMS`). Publishing is part of the batch only when the user asks.
- The `upstream` remote has `tagOpt --no-tags`, because upstream's own `v0.x` tags collide with Claudex versions. Merge fixes with `git fetch upstream; git merge upstream/main`.
- `CODEX_HANDOFF.md` (candid state for the next agent) and a short `AGENTS.md` pointing to it live in the root, gitignored. Keep the handoff current when work changes.
- Build and test: `run.cmd` (or `run.cmd -Demo`), `dotnet test tests/Claudex.Tests`. Needs the .NET 10 SDK.
- The Claude status line argument is `--claudex-statusline`, which keeps it apart from Developer Island's `--claude-statusline`.
- UX: never show "~" before numbers; estimates are marked in words (e.g. "estimated" next to the reset time).
