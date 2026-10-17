# Claudex

Personal fork of [Developer Island](https://github.com/maximilian467/Developer-Island) by Maximilian Köhlenbeck. Private repo, not published.

## License and credit
- Claudex is **MIT**, inherited from Developer Island. Do not switch it to GPL-3.0 (the default for soybean_alien's own projects).
- Never edit or remove Maximilian Köhlenbeck's copyright line in `LICENSE`.
- Keep crediting him in the README banner and Credits section, `THIRD_PARTY_NOTICES.md`, and the GitHub description/homepage.
- Credit the user as "Claudex by soybean_alien", with no "Copyright ... soybean_alien" line.

## Workflow
- Every change gets a `CHANGELOG.md` entry (Keep a Changelog), a SemVer bump in `Directory.Build.props` and a `vX.Y.Z` tag.
- The `upstream` remote has `tagOpt --no-tags`, because upstream's own `v0.x` tags collide with Claudex versions. Merge fixes with `git fetch upstream; git merge upstream/main`.
- No `CODEX_HANDOFF.md` / `AGENTS.md` for this project.
- Build and test: `run.cmd` (or `run.cmd -Demo`), `dotnet test tests/Claudex.Tests`. Needs the .NET 10 SDK.
- The Claude status line argument is `--claudex-statusline`, which keeps it apart from Developer Island's `--claude-statusline`.
