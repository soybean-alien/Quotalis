# Privacy

What Quotalis reads, what it stores and what leaves your PC. The short version is in the [README](../README.md#privacy).

**Local-first. Quotalis never uploads your prompts or code.** (The AI tools themselves talk to their providers as usual; Quotalis only reads what they leave on your disk.)

- No Quotalis account, telemetry or cloud sync. Network traffic is what you set up: claude.ai's usage answer while signed in for Claude plan usage; Codex's ChatGPT sign-in and account rate-limit requests while connected; the Nous Portal account read (your wallet balance) while signed in for the Hermes column; the copy of your server's Hermes export over SSH, if you set one up; the update check against GitHub's public releases (no account; the GitHub CLI only while the repository is private); and the GitHub heatmap through the GitHub CLI.
- The app reads local JSONL files that can also contain conversation content, but extracts only usage and session metadata: token counts, model, timestamps, session IDs and the working directory. It does not retain, display, log or upload prompt, message or code content.
- The local database (`%LOCALAPPDATA%\Quotalis\usage.db`) contains token counts, model names, timestamps, session IDs, project folder names, and daily totals (a `focus_sessions` table from earlier versions stays empty).
- `settings.json` holds your settings, including repository paths, favorites, the auto-hide app list and the island's monitor (its Windows device name and hardware id); `ui-state.json` remembers the module you opened last.
- Camera and microphone marks read Windows' own record of which apps use those devices (the capability consent store in the registry, as Windows' privacy indicator does). Only "in use or not" is kept; app names are never stored, shown or logged, and nothing is recorded.
- `claude-plan.json` (in `%USERPROFILE%\.quotalis`, with the calibration log and hook traces) holds the last Claude plan percentages and reset times, read from claude.ai or handed over by Claude Code's status line, nothing else (no account details, organization, paths, model, cost or session).
- `claude-signin` (in `%USERPROFILE%\.quotalis`) is the WebView2 browser profile of the claude.ai sign-in: claude.ai's cookies and cache, on this PC only. You sign in on claude.ai's own page; Quotalis never sees your password, never reads the cookie values, and never logs claude.ai's answers (only HTTP status codes and error types). Settings, Sign out clears it.
- `chatgpt-signin` (in `%USERPROFILE%\.quotalis`) is a separate `CODEX_HOME` for the locally installed Codex app-server. Codex handles the browser sign-in and stores credentials in that profile; Quotalis does not read or log them. Quotalis requests account rate limits, retains the returned bucket names, percentages and reset times in memory, and publishes the shared Work/Codex windows to `codex-plan.json`. Account polling does not add entries to the local Codex limit history log. It does not retain account identifiers or raw account responses. Settings, Sign out asks Codex to log out of this profile. Reading rate limits sends no AI messages.
- **Hermes column.** `nous-signin.bin` (in `%USERPROFILE%\.quotalis`) is Quotalis's Nous Portal sign-in, encrypted with Windows' data protection for your account. Its tokens could run paid models, so Quotalis keeps them only there and uses them only for `GET /api/oauth/account`, the account read that returns your wallet; the answer's email, organisation and ids are never stored or logged, only the dollar amounts, plan name and renewal date are kept in memory. `hermes-wallet.json` holds the wallet bar's scale (the amount after the last top-up and the last reading). Hermes's token use comes from Hermes's own database on this PC, read-only, or from a server's export (`hermes-usage.json` in `%LOCALAPPDATA%\Quotalis`); either way only counts, costs, times, model and surface names and tool names are read, never message text, reasoning, prompts, titles or chat ids. Session ids in the export are one-way hashes.
- Logs (`%LOCALAPPDATA%\Quotalis\logs`) contain app events and error types, never content, repository names, tokens or amounts. They are kept for 7 days.
- The API equivalent uses a static price catalog and exchange rate. You can adjust both in `%USERPROFILE%\.quotalis\pricing.json`, for example:

  ```json
  { "models": { "your-model-id": { "input": 2.0, "output": 8.0 } } }
  ```

  These are illustrative values, not a current price quote. Prices are USD per million tokens and load on startup.
