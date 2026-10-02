# Lupa Fiscal

Open-source (MIT) semantic search over the public binding rulings (informações vinculativas) published by the Portuguese tax authority. Ask in plain Portuguese, get the closest rulings with the relevant passage and a link to the official PDF. Working name; it must never suggest any affiliation with the tax authority.

## Stack & essentials
.NET 10 (minimal API, crawler, indexer, ONNX Runtime embeddings), SQLite (FTS5 + vectors), Angular UI. Windows-native, no external services, 0 EUR.

## Commands
Run from the repository root. Data goes to `data/` (override with `--data-dir` or `LUPAFISCAL_DATA_DIR`).
- Build: `dotnet build LupaFiscal.slnx`
- Test (offline, synthetic fixtures only): `dotnet test LupaFiscal.slnx`
- Crawl (polite, resumable; about 25 min for CIRS): `dotnet run --project src/LupaFiscal.Cli -- crawl --tax CIRS` (options: `--retry-failed`, `--use-cached-listing`, `--interval-seconds N` with N >= 1, `--max-retries N`, `--max-downloads N`)
- Re-extract text from cached PDFs (offline, after an extraction change): `dotnet run --project src/LupaFiscal.Cli -- extract --tax CIRS`
- Corpus status (exit 0 only when no listed ruling is pending): `dotnet run --project src/LupaFiscal.Cli -- corpus-status --tax CIRS` (add `--list-failed` for reasons)
- Download the embedding model (pinned revision, SHA-256 verified, no account; also run by the first `index`): `dotnet run --project src/LupaFiscal.Cli -- model download`
- Build or update the index (idempotent, resumable; about 5 min for CIRS on CPU): `dotnet run --project src/LupaFiscal.Cli -- index` (optional `--tax CIRS`)
- Index status (exit 0 only when every extracted ruling has chunks and every chunk has a vector): `dotnet run --project src/LupaFiscal.Cli -- index-status`
- Search: `dotnet run --project src/LupaFiscal.Cli -- search "Posso deduzir as despesas de educação dos meus filhos no IRS?"` (options: `--tax T`, `--article A`, `--year Y`, `--limit N` from 1 to 50, `--mode hybrid|keyword|vector`)
- Retrieval eval (recall@10 and MRR@10 for keyword, vector and hybrid; writes `docs/eval/report.md` and `docs/eval/history.json`, leaving them unchanged when the scores are unchanged; the question set is frozen, see `eval/README.md`): `dotnet run --project src/LupaFiscal.Cli -- eval --questions eval/questions.json --out docs/eval/report.md --min-recall 0.70` (options: `--label L`, `--note N` to name and describe a tuning iteration)
- Latency benchmark (50 hybrid queries after one warm-up, prints p50, p95 and max; add `--record` to write them to the report): `dotnet run --project src/LupaFiscal.Cli -- bench --questions eval/questions.json --max-ms 1000`
- Run the API (needs the index and model; listens only on http://localhost:4401; also serves the built UI from `web/dist` when present): `dotnet run --project src/LupaFiscal.Api` (options: `--data-dir D`, `--web-root W`). Endpoints: `GET /api/search?q=&tax=&article=&year=&limit=` (limit 1 to 50, default 10; invalid input returns a 400 problem), `GET /api/facets`, `GET /api/health`.
- Web UI (Angular 21 in `web/`; install once with `npm --prefix web ci`, and `npm ci` at the root for the Playwright tooling):
  - Build: `npm --prefix web run build` (output `web/dist`, served by the API on 4401)
  - Test (Vitest, runs once): `npm --prefix web test`
  - Dev server: `npm --prefix web start` (http://localhost:4400, proxies `/api` to the API on 4401, which must be running)
  - UI evidence: `npm run ui:evidence` (needs `dotnet build LupaFiscal.slnx`, the web build and the index; starts the API on 4401, drives the UI with the installed Playwright Chromium, saves screenshots at 1440 and 390 px to the gitignored `web/test-results/evidence/`, exits non-zero on a failed assertion). `npm run ui:evidence:publish` runs the same checks and writes the screenshots to `docs/evidence/` instead.
  - Design directions: `npm run ui:directions` (re-renders the three mocks in `docs/ui/` at 1440 and 390 px with the installed Chromium)
- Docs and copy checks (offline): `node scripts/validate-docs.mjs` (exits 1 on missing STATE front-matter keys, an em-dash in README, CONTRIBUTING, WALKTHROUGH or `web/src`, a WALKTHROUGH over 1100 words, a missing README image, a non-MIT LICENSE, or a tracked file name containing token or secret); its tests: `node --test scripts/validate-docs.test.mjs`
- Gate 0 probe (at most 5 polite requests to the live site; never in CI): `node scripts/gate0-probe.mjs`
- CI: `.github/workflows/ci.yml` on windows-latest runs the docs checks, `dotnet build` and `dotnet test` (fixture corpus, fake embedder), `npm --prefix web ci`, build and test. It never crawls, never downloads the model and uses no secrets.

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
