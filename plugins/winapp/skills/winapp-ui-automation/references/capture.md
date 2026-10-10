# Recording video

### Record video (H.264 MP4)
Record a window or element region to MP4. Prefer a positive `--duration-sec N` for
agents and scripts; otherwise recording waits for a stop signal. npm helpers require
`durationSec` (integer 1–86400). Their abort signal is forceful cancellation, not graceful stop.
```powershell
# Record a window for 10s at 15 fps
winapp ui record -a myapp --duration-sec 10 --fps 15 --output demo.mp4

# Recommended agent evidence: MP4 plus timestamped JPEGs and an NDJSON index
winapp ui record -a myapp --frames --duration-sec 10 --fps 10 --output evidence.mp4 --json

# Include overlays/popups (captures from screen DC; may include occluding windows)
winapp ui record -a myapp --capture-screen --duration-sec 5 --output with-popups.mp4

# Programmatic stop: pipe a newline to stop and finalize the MP4 (for agent/script callers)
"" | winapp ui record -a myapp --json --output capture.mp4
```
- Default `--duration-sec 0` records until Ctrl+C, a newline, or EOF on redirected stdin.
- `--frames` writes `<output-name>.frames` with a manifest, NDJSON index, and changed JPEGs. It supports 1-30 fps and `--max-edge` 64-4096 (default 1280), with a 1 GiB cap. Use `elapsedMs` to bound transitions.
- Existing recording outputs are rejected by default. Use a fresh path, or explicitly
  request `--overwrite` to replace them after the new take finishes. Previous frame
  directories are archived, not deleted. On partial failure, keep the reported evidence
  and follow `recoveryHint`.
- `--capture-screen` captures from the screen DC so overlays and popups are included; the window is brought to the foreground first. When WGC is unavailable and `--capture-screen` is not passed, the CLI returns an error — re-run with `--capture-screen` to consent to screen-DC capture. Because the screen DC captures whatever is genuinely in front, the target's foreground is **verified immediately before capture**; if activation was refused the command fails with `foreground_not_target` and writes nothing rather than returning an image of the wrong window.
- Providing a selector that doesn't match any element fails immediately with `element_not_found` (rather than silently recording the whole window).
- A selector for an item in an open menu, flyout, tooltip, or teaching tip records from that item's popup window, not the window behind it.
- `--json` writes the final result to stdout and one JSON event per line to stderr.
