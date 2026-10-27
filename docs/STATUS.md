# Status

What was verified, how, and what is still open. Updated 2026-10-25 for 0.7.0 (Music, Focus, Tasks, Calendar, System, Awake, Home Cloud and the GitHub module were removed; their checks are gone from this page).

## Verified

| Area | How |
|---|---|
| Unit and integration tests | 420 tests pass (`dotnet test`, 2026-10-25), including a real `git` repository, the Hermes export and wallet parsers, the Nous poll schedule and the island state machine's transitions. |
| Release build | `tools/build-release.ps1` builds tests, publish, portable zip and installer in a shell whose PATH has no .NET 10 SDK (the script finds it). |
| Every island state | Rendered offscreen with `--demo --snapshot` (41 renders for 0.7.0, checked by eye): compact, peek, activities, notch, the Usage tab (Claude, Codex, Hermes) and the Git tab, the usage-graph tooltip, empty and error states, each featured module, favorite rotation, camera and microphone marks in compact, notch, activity and expanded, every Settings page. |
| Camera and microphone | The consent store was read on this machine with the former `--system-report` switch (removed in 0.7; both "not in use" at the time). The in-use rule and the island behavior (marks in every state, dots in the notch, notch instead of hide, never opening) are covered by tests. |
| Manual calibration | `--quotalis-calibrate` with the desktop usage card's numbers: prints "applied by Quotalis", saves both windows and logs the reading; refuses a window without a reset time (exit 1). |
| Codex Stop hook | `--quotalis-codex-stop-hook` on a live Codex rollout log: blocks once with that session's numbers filled into the 🔵 line, then stays quiet for 30 seconds ("reported recently"). |
| Claude plan bridge | `--quotalis-statusline` fed with a sample in the documented format: exit 0, no output, only percentages and reset times stored (no path, model, cost or session). |
| Auto-hide | Earlier iteration: Chrome maximized in front retracted the island live. Fast switching (Chrome, VS Code, Chrome; a five-app chain; late stale events; missed events) is covered by tests of `AutoHideCoordinator`; it was not re-checked by hand, since input is never simulated. One rule for every module: tests cover the resting island with different featured modules and the open island, with Chrome and VS Code in front. |
| Claude plan usage | 2026-10-22 (0.5.14): a real claude.ai sign-in read 5h 6 % and weekly 4 %, matching claude.ai's own Usage page. |
| ChatGPT plan usage | 2026-10-23 (0.5.16): the owner compared the readings with Codex's usage view; they matched. |
| Hermes column | 2026-10-25 (0.6.1): a live Nous sign-in read the wallet, the balance matched the Nous Portal, and a token renewal was seen working. |
| Performance | See [PERFORMANCE.md](PERFORMANCE.md). |

## Not verified on real hardware or accounts

- The terminal status line as a second Claude plan source with a Claude subscription in a terminal session (Claude Code was used through the VS Code extension, which does not run status lines).
- Camera and microphone marks with a device actually in use (requires starting a call or recording; the change notification and the in-use rule are implemented and tested separately).
- Hover and click timing by hand: input was never simulated; behavior is covered by state machine tests.

## Known limitations

- The claude.ai usage endpoint is not a public API: if claude.ai changes it, Quotalis shows "unknown format" and no number until it is updated. After a window resets, the island shows no percentage until the next reading.
- When Claude Code already has a status line, Quotalis does not replace it; the status line source then needs a manual pipe into `Quotalis.exe --quotalis-statusline`.
- The camera and microphone marks disappear while the island is hidden from the tray or for a fullscreen app (both are hides you asked for); Smart Auto-Hide keeps the notch instead.
- One placement is saved, not a layout per display. ARM64, Windows 10 and clean-machine installation still need independent validation.
