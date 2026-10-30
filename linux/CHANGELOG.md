# Changelog

Quotalis for Linux. Format: [Keep a Changelog](https://keepachangelog.com), versions: [SemVer](https://semver.org).

## [0.1.2]

### Changed
- On Omarchy's bar the tools take turns every 7 seconds: Claude, Codex, Hermes when it's on, then today's cost. All of them stay in the tooltip, and installing over an older version moves the old 30 second interval to 7.

### Fixed
- The last character on Omarchy's bar no longer loses its right edge at fractional display scales.

## [0.1.1]

### Changed
- The screen is more compact, 43 columns instead of 76: the word "resets" is gone, with shorter bars and tighter spacing.
- The window the bar's click opens now fits its text, with the same margin on all four sides, on Hyprland 0.55 or later.

## [0.1.0]

### Added
- First Linux version: service, terminal screen, Omarchy bar and Waybar modules, setup and removal, tarball and `PKGBUILD`.
