# Performance

Quotalis runs all day, so idle cost matters more than peak speed.

## Measurements

### Iteration 3 (2026-10-01)

Lenovo laptop (i7-13700H, Intel Iris Xe, battery), Windows 11, no administrator rights. This iteration measured the System module's sensor sampling and a ticking Focus timer; both modules were removed in 0.7, so those figures no longer apply. What still applies from it:

| Scenario | Result |
|---|---|
| Compact values with tabular figures and width reserves | A ticking value no longer re-measures and re-morphs the capsule (the demo island's CPU over 45 s fell from 1.2-2.0 s to 0.84-0.95 s) |
| Disabled modules | No timers, watchers or processes (covered by tests) |

### V1

Windows 11 (26200), 1920 × 1200 at 125 % plus a 2560 × 1440 second monitor, Release build, self-contained, x64.

| Scenario | Working set | Private bytes | CPU |
|---|---|---|---|
| Idle, compact, normal mode, 60 s window | **55 MB** | 79 MB | **16 ms / 60 s (0.03 % of one core)** |
| Idle, compact, before the optimizations below | 204 MB | 118 MB | 250 ms / 30 s (0.8 %) |

Other timings:

| Scenario | Time |
|---|---|
| First scan: 42 Claude transcripts, about 14k events | 1.6 s, on a background thread |
| Warm start: only one changed transcript re-read (cursors) | 47 ms |
| Morph (compact to expanded) | About 250 ms spring on the compositor thread |

Measured with `Get-Process` over the stated window, after the startup scan.

## What keeps it cheap

- **Modules that are off do nothing.** No watchers, timers or child processes. Git starts `git` only while its panel is open (or once for a new repository); the favorite rotation runs only while the compact capsule is visible; the outside-click hook exists only while expanded.
- **Event-driven only.**
  - FileSystemWatcher events feed the providers.
  - One-shot timers handle midnight and the active-flag timeout.
- **Account reads only as often as they are useful.** claude.ai is read a few seconds after a Claude reply (at most every 15 s) and every 2 minutes otherwise; ChatGPT every 2 minutes or shortly after Codex activity. The Nous wallet is read every second only while Hermes works, every 5 minutes once it has been quiet for 5 minutes, and never while the PC is locked or asleep; Hermes's token use once a minute. Failures back off.
- **Camera and microphone without polling.** One background thread waits on two registry change notifications and reads the consent store only after Windows signals a change.
- **Auto-hide fallback check.** While auto-hide can apply, a 750 ms timer reads the foreground window (a handful of Win32 calls, a cached process name); it never runs when auto-hide is off or the island sits elsewhere.
- **No re-layout on ticks.** Compact values reserve their widest form, so a ticking figure no longer resizes and re-morphs the capsule.
- **No rendering loop.** All motion is composition animation (springs, key frames and expressions). Nothing redraws while idle, and there are no perpetual animations.
- **Incremental work.**
  - File cursors mean only appended bytes are parsed.
  - Lines are pre-filtered by substring before any JSON parsing.
  - Daily totals are running accumulators.
  - The history graph recolours its cells in place.
- **Memory trim after the first scan.** One compacting GC and an `EmptyWorkingSet` call drop the start-up peak.
- **Lean runtime.** Only the needed Windows App SDK components ship (no AI, ML, ONNX or Widgets), about 45 MB less.
- **Runtime settings.** Workstation GC without concurrent background threads, no tiered PGO instrumentation, and ReadyToRun for faster start.

## Reproduce

```powershell
$p = Start-Process .\Quotalis.exe -PassThru; Start-Sleep 25
$c1 = $p.TotalProcessorTime; Start-Sleep 60; $p.Refresh()
"{0:N1} MB, CPU {1} ms" -f ($p.WorkingSet64/1MB), ($p.TotalProcessorTime - $c1).TotalMilliseconds
```
