# Lupa Fiscal

Open-source (MIT) semantic search over the public binding rulings (informações vinculativas) published by the Portuguese tax authority. Ask in plain Portuguese, get the closest rulings with the relevant passage and a link to the official PDF. Working name; it must never suggest any affiliation with the tax authority.

## Stack & essentials
.NET 10 (minimal API, crawler, indexer, ONNX Runtime embeddings), SQLite (FTS5 + vectors), Angular UI. Windows-native, no external services, 0 EUR.

Commands: to be defined by the first implementation run (record them here).

## Invariants
- Source code, identifiers, comments and logs in English. UI copy in European Portuguese.
- No generated answers in v0.1: results are always passages from cited official rulings. Not tax advice; the UI shows that notice.
- Crawler is polite: respects robots.txt, rate limited, descriptive User-Agent, resumable, disk cache.
- Local data lives under `data/` (corpus cache, index database, model files) and is never committed. Tests use a small fixture corpus; CI never crawls.
- Never use `token` or `secret` in a file name: those patterns are gitignored as credentials (name a tokenizer e.g. `WordPieceEncoder.cs`).
- No secrets, no new accounts, no paid services. Sponsor gates: making the repo public, public hosting, any paid model.
- Local ports in the 44xx range.

## Verification
Done requires passing build and tests, plus evidence: eval numbers (recall@10, MRR) for retrieval changes, Playwright screenshots at 1440 and 390 px for UI changes. Reports separate what was confirmed by evidence from what was not verified.

## Where things live
- Current state, decisions, roadmap: `docs/STATE.md` (no Linear; this project is small enough)
- Architecture and trade-offs: `docs/WALKTHROUGH.md`
- Design reference: `DESIGN.md` (created by the design phase); direction mocks in `docs/ui/`
- UI evidence: `docs/evidence/`
- Work tracking: `docs/STATE.md`
- Tooling configured here: none beyond project packages (Playwright via npm) — catalog: `C:\Users\User\Desktop\PLAYBOOK\TOOLING.md`

<!-- forja-core:begin -->
## FORJA core
New FORJA tasks use Core. The conversation agent prepares the goal, starts the controller and reports its result; it does not act as Lead or manually dispatch the legacy crew.
Resolve `<forja>` from the caller-provided installation, `FORJA_ROOT`, or an existing FORJA hook path in `.claude/settings.json`. If unavailable, ask for the installation path; do not guess or install another copy.
Read `<forja>/docs/CORE.md` and `<forja>/docs/CORE-RUNBOOK.md`. From this project: `node "<forja>/bin/forja.mjs" start --goal "..." --provider claude|codex|kilo`. Supply the explicitly selected profile with `--config`; installing or updating FORJA does not select models.
The controller owns planning, development, checks and independent review. Workers read only the phase and applicable domain methods supplied in `specialist_context`. Do not load `forja-lead` or other legacy crew skills for Core work.
Core state and usage live in `.forja/`. Inspect existing changes before starting; preserve them and use `--allow-dirty` only when work on that snapshot is authorized. Git delivery requires explicit configuration and the controller delivery contract.
Preparation does not start a run. Never silently resume or replace an active Core or legacy run. Stop existing executors before an explicitly authorized handover; preserve their state and unfinished work.
Legacy `runner` and `run start` are compatibility commands only when explicitly requested. Their state remains in `docs/forja/`; read legacy methods as files for that workflow. Restart the conversation after migration to discard previously loaded legacy instructions.
<!-- forja-core:end -->
