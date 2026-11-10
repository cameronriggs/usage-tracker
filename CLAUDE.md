# Usage Tracker

Tiny Windows widget showing 5-hour and weekly AI usage limits. Public repo
`cameronriggs/usage-tracker`. Read `README.md` for what it does and how config works.

## Working on it

- All code is `src/UsageTracker.cs`. It is compiled by `build.ps1` with the
  `csc.exe` from .NET Framework 4.8, which only supports **C# 5**: no `$""`
  strings, no `?.`, no `=>` members, no `nameof`, no property initializers.
- Exit the running widget before building, or the exe can't be overwritten.
- Check layout changes without opening a window:
  `UsageTracker.exe --render out.png --demo --scale 2` (demo data) or without
  `--demo` (live data). Run it with `Start-Process -Wait`, since it's a GUI exe.
- Small icon frames must stay 32-bit bitmaps, not PNG: .NET Framework's `Icon`
  class reads small PNG frames as noise (the tray icon uses it).
- Never print or log the Claude token, and never refresh or rewrite
  `.credentials.json`: rotating its refresh token can sign Claude Code out.

## Releases

The exe is not committed. Bump `AssemblyVersion`/`AssemblyFileVersion` in the
source, build, then `gh release create vX.Y.Z UsageTracker.exe`.

## Commits

No `Co-Authored-By: Claude` trailer (or any attribution) on commit messages or PRs.
