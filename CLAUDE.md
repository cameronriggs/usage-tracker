# Usage Tracker

Tiny Windows widget showing 5-hour and weekly AI usage limits. Public repo
`cameronriggs/usage-tracker`. Read `README.md` for what it does and how config works.

## Working on it

- All code is `src/UsageTracker.cs`. It is compiled by `build.ps1` with the
  `csc.exe` from .NET Framework 4.8, which only supports **C# 5**: no `$""`
  strings, no `?.`, no `=>` members, no `nameof`, no property initializers.
- Exit the running widget before building, or the exe can't be overwritten.
- Check layout changes without opening a window:
  `UsageTracker.exe --render out.png --demo --scale 2 --layout wide|narrow|strip`
  (add `--taskbar light|dark` for the strip). Run it with `Start-Process -Wait`,
  since it's a GUI exe.
- **Use `--demo` for renders.** Without it every render calls Anthropic's usage
  endpoint, which rate-limits per account (Claude Code's own usage display shares
  the allowance). Five calls in ten minutes got a 429 on 2026-11-10.
- Small icon frames must stay 32-bit bitmaps, not PNG: .NET Framework's `Icon`
  class reads small PNG frames as noise (the tray icon uses it).
- Never print or log the Claude token, and never refresh or rewrite
  `.credentials.json`: rotating its refresh token can sign Claude Code out.

## How the layouts work

- `Renderer` lays out Wide, Narrow and Strip (taskbar). Measure text with
  `TextRenderer.MeasureText(graphics, ...)`: the overload without a device
  context ignores `NoPadding` and adds about 8 px per string.
- Wide and Narrow are a normal window (GDI text, DWM rounded corners, `Form.Opacity`).
- Taskbar mode is a per-pixel-alpha layered window: `WS_EX_LAYERED` from
  `CreateParams` plus `UpdateLayeredWindow` in `PushLayered()`, so it draws with
  GDI+ text (GDI text breaks alpha). Switching modes calls `RecreateHandle()`.
  Keep `Form.Opacity` at 1 in that mode, or WinForms calls
  `SetLayeredWindowAttributes` and `UpdateLayeredWindow` stops working.
- In taskbar mode a 1.5 s timer plus a foreground WinEvent hook re-raise it
  above the taskbar, follow display changes, and hide it for full-screen apps.
  `WM_MOVING` pins dragging to the taskbar strip.

## Releases

The exe is not committed. Bump `AssemblyVersion`/`AssemblyFileVersion` in the
source, build, then `gh release create vX.Y.Z UsageTracker.exe`.

## Commits

No `Co-Authored-By: Claude` trailer (or any attribution) on commit messages or PRs.
