# Usage Tracker notes

## 2026-11-25: per-model weekly bars (Fable), v1.4 (released)

- Added a row for each per-model weekly limit Anthropic reports, e.g. "Fable".
- Source: the usage endpoint's `limits[]` array, entries with
  `kind: "weekly_scoped"`, `scope.model.display_name`, `percent` (0-100) and
  `resets_at`. Found by reading Claude Code's own `/usage` code in
  `~/.local/bin/claude.exe`; a direct test call to the endpoint was blocked by
  the permission classifier, so the live shape is not seen yet.
- `Limit.Model` marks a single-model limit. `Limit.Order` sorts all-model
  limits first; `usageFile` still writes only the all-model windows.
- Taskbar strip now adds a column when a provider has 3+ limits (it used to
  drop everything past 2 rows).
- Committed, pushed and released as v1.4.0. If the Fable row never shows with
  real data, check the live reply's shape (the parser is `AddModels`).
