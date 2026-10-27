# Releasing

Releases are built locally and published as GitHub releases with three files: the installer, the portable zip and their SHA-256 checksums.

## Checklist

1. **Version.** Set `<Version>` in [`Directory.Build.props`](../Directory.Build.props) (semantic versioning: `0.1.0`, `0.1.1`, `0.2.0`).
2. **Changelog.** Move the entries under "Unreleased" in [`CHANGELOG.md`](../CHANGELOG.md) to a new section with the version and date.
3. **Build.** Quit any Quotalis that runs from `artifacts/publish`, then:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools/build-release.ps1
   ```

   It runs the tests and writes to `artifacts/`. Then check that nothing personal is in the build:

   ```powershell
   powershell -ExecutionPolicy Bypass -File tools/privacy-scan.ps1 artifacts/publish artifacts/Quotalis-<version>-win-x64-portable.zip
   ```


   | File | What it is |
   |---|---|
   | `Quotalis-Setup-<version>.exe` | Per-user installer (no administrator rights, self-contained) |
   | `Quotalis-<version>-win-x64-portable.zip` | Portable build; extract everything and run `Quotalis.exe` |
   | `SHA256SUMS-<version>.txt` | Checksums of both |

4. **Smoke test** on a machine (or a fresh user account) without the .NET SDK: install, start, open each module in normal and demo mode, check Settings, About shows the version, uninstall.
5. **Privacy scan of the commit** (every file Git would track): `powershell -ExecutionPolicy Bypass -File tools/privacy-scan.ps1`. Fix every finding first.
6. **Commit and tag:**

   ```powershell
   git commit -am "release: 0.1.0"
   git tag -a v0.1.0 -m "Quotalis 0.1.0"
   git push origin main v0.1.0
   ```

7. **Publish** the release on GitHub (Releases, Draft a new release, tag `v0.1.0`), with the changelog section as notes and the three files attached. With the [GitHub CLI](https://cli.github.com):

   ```powershell
   gh release create v0.1.0 --title "Quotalis 0.1.0" --notes-file notes.md `
     artifacts/Quotalis-Setup-0.1.0.exe `
     artifacts/Quotalis-0.1.0-win-x64-portable.zip `
     artifacts/SHA256SUMS-0.1.0.txt
   ```

## Notes

- The installer is not code-signed yet, so Windows SmartScreen asks for confirmation on first run. Mention this in the release notes.
- Only x64 is built and tested. `-Runtime win-arm64 -SkipInstaller` produces an experimental ARM64 zip that has not been validated.
- Uninstalling keeps `%LOCALAPPDATA%\Quotalis` (history and settings), so a reinstall continues where it left off.
