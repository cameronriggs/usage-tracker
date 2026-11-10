# Usage Tracker

A tiny Windows widget that shows the 5-hour and weekly usage limits for your AI
agents. One 150 KB exe, about 10 MB of RAM, no installs.

```text
▌Claude    5h ██░░░░░░░░│░░░   7%  3h 21m
▌Max 5x    wk ██░░░░░░│░░░░░  12%  2d 15h
 ─────────────────────────────────────────
▌Codex     5h ███████████│░░  76%  41m
▌Plus      wk ██████████████  93%  1d 5h
```

## Get it

Download `UsageTracker.exe` from the
[latest release](https://github.com/cameronriggs/usage-tracker/releases/latest)
and put it in its own folder (it keeps its settings next to itself). Windows 10
or 11; it uses the .NET Framework 4.8 that Windows already has.

The exe isn't code-signed, so Windows SmartScreen may warn the first time. Click
**More info**, then **Run anyway**. Or build it yourself from source (see Build).

## Run it

Double-click `UsageTracker.exe`. It appears at the bottom right of your main
screen. Running it again just brings the open one to the front.

You need the agents' own tools signed in on the same PC: Claude Code for
Claude, and the Codex CLI or VS Code extension for Codex.

## Using it

- **Drag** anywhere on it to move it. Position is remembered.
- **Right-click** (the widget or the tray icon) for:
  - Hide / Show
  - Refresh now
  - Always on top
  - Show reset times
  - Size: Small, Normal, Large, Extra large
  - Opacity: 50 to 100%
  - Start with Windows
  - Edit providers (`config.json`)
  - Exit
- **Left-click the tray icon** to bring the widget back if it's hidden or covered.
- **Hover** a provider for details: exact reset time, how much of the window has
  passed, when it last updated, and any error.

How to read a bar:

- Fill is the share of the limit used. Blue under 70%, amber 70 to 89%, red 90%+.
- The thin white tick is how much of the window's *time* has passed. Fill past
  the tick means you're burning faster than the limit allows on average.
- The right column counts down to the reset.
- A dimmed provider with an amber dot is showing its last good numbers because
  the latest refresh failed (hover to see why). A blue dot means it's refreshing.

It refreshes every 3 minutes, right after a window resets, and after the PC wakes.

## Providers (`config.json`)

`config.json` sits next to the exe and is created on first run. Edits apply as
soon as you save. Add as many providers as you like, in display order.

```json
{
  "refreshMinutes": 3,
  "providers": [
    { "type": "claude", "name": "Claude" },
    { "type": "codex",  "name": "Codex" }
  ]
}
```

Every provider takes `name`, optional `color` (`"#RRGGBB"` for the side stripe)
and optional `"enabled": false` to hide it without deleting it.

| type | Where the numbers come from | Extra options |
| --- | --- | --- |
| `claude` | Claude Code's login (`~/.claude/.credentials.json`) and Anthropic's usage endpoint, the same data `/usage` shows. Covers claude.ai and Claude Code, since they share limits. | `credentials`: path to another account's `.credentials.json` |
| `codex` | Runs `codex app-server` and asks it for `account/rateLimits/read`. These are your ChatGPT plan's Codex limits. Finds Codex in the VS Code extension, or on PATH. | `codexPath`: a specific `codex.exe`. `codexHome`: a different `CODEX_HOME` (second account) |
| `command` | Any command that prints JSON. Use it for agents without built-in support. | `command`: run with `cmd /c`, from this folder |

A `command` must print:

```json
{
  "plan": "Pro",
  "limits": [
    { "percent": 42, "windowMinutes": 300,   "resetsAt": "2026-11-10T17:40:00Z" },
    { "percent": 18, "windowMinutes": 10080, "resetsAt": 1794700000 }
  ]
}
```

`resetsAt` is ISO 8601 or unix seconds. `windowMinutes` sets the label (300 is
`5h`, 10080 is `wk`) and the pace tick; `"label": "mo"` overrides the label.
Print `{"error": "message"}` to show a message instead.

## Privacy and safety

- The Claude token is read from disk and sent only to `api.anthropic.com`. The
  widget never refreshes or rewrites it, so it can't sign Claude Code out. If
  Claude Code hasn't run for a few hours the token expires and the widget says
  so; opening Claude Code fixes it.
- Codex handles its own login. The widget never sees the token.
- Anthropic's usage endpoint isn't a public API. If it changes, the Claude row
  will show an error until the code is updated.

## Files

| File | What it is |
| --- | --- |
| `UsageTracker.exe` | The app |
| `config.json` | Your providers (hand-edited) |
| `state.json` | Window position, size, opacity, on-top (written by the app) |
| `error.log` | Only appears if something unexpected breaks |
| `src/UsageTracker.cs` | All the source |
| `build.ps1` | Rebuilds the exe with the C# compiler built into Windows |
| `app.ico` | Icon, drawn by `build.ps1` if missing |

## Build

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Exit the widget first. Uses `csc.exe` from .NET Framework 4.8, so the source is
C# 5. A hidden `--render out.png [--demo] [--scale 2]` switch draws the widget to
an image, for checking layout changes without opening a window.
