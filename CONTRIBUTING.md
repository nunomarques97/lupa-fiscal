# Contributing to Lupa Fiscal

Thank you for helping. Issues and pull requests are welcome. Please read this page first: a few rules protect the people who rely on the rulings and the site that publishes them.

## Setup

You need Windows 10 or 11, the [.NET SDK 10](https://dotnet.microsoft.com/download) and [Node.js](https://nodejs.org/) 22 or 24. Run everything from the repository root.

```
dotnet build LupaFiscal.slnx
npm --prefix web ci
npm ci
```

`npm ci` at the root installs the Playwright tooling used for screenshots. It uses an existing Playwright Chromium; install one with `npx playwright install chromium` only if you do not have it.

You only need real data to run the search locally or to measure retrieval: follow "Build and run" in the [README](README.md). Real data stays in `data/`, which is ignored by Git.

## Tests

Every change must keep these green. They run offline, on a small synthetic corpus, with a fake embedder:

```
dotnet test LupaFiscal.slnx
npm --prefix web test
npm --prefix web run build
node scripts/validate-docs.mjs
node --test scripts/validate-docs.test.mjs
```

Tests that need the real embedding model run only when it is in `data/models`, and are skipped otherwise (as in CI).

Depending on what you change, also bring evidence:

- **Search quality** (chunking, embeddings, ranking): run `eval` and `bench` (see the README) and put the recall@10, MRR@10 and latency before and after in the pull request. The question set in `eval/questions.json` is frozen; do not edit it to improve a score (see [eval/README.md](eval/README.md)).
- **Interface**: follow [DESIGN.md](DESIGN.md) and run `npm run ui:evidence`, which checks the main screens in a real browser and saves screenshots at 1440 and 390 px. Attach the ones you changed.

CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)) runs the same checks on Windows. It never crawls and never downloads the model.

## Polite crawling

The rulings are published by a public body on a shared website. The crawler must stay a well-behaved guest:

- Respect `robots.txt`. It is fetched at the start of every run and obeyed if it appears or changes.
- Never make more than one request per second. `--interval-seconds` cannot go below 1; do not work around it.
- Keep the descriptive User-Agent with a link to this repository.
- Download each PDF once. Everything is cached in `data/corpus/`, and runs resume where they stopped.
- Back off on errors: 429 and 5xx responses are retried with increasing waits, and a run stops after repeated failures.
- Contact only the allowlisted host over https.
- Never crawl in tests or in CI. Tests use synthetic listings and PDFs generated in the test code. Do not commit real rulings, PDFs or extracted text.

## No secrets

The project needs no account, key or paid service, and must stay that way.

- Never commit credentials, `.env` files, keys or certificates. `.gitignore` already blocks the usual ones.
- File names containing `token` or `secret` are reserved for credentials and are ignored by Git. Name things differently (for example `WordPieceEncoder.cs`, not `Tokenizer.cs`). `node scripts/validate-docs.mjs` fails if such a file is tracked.
- Do not add dependencies on external services or new paid components.

## Language conventions

- **Code in English**: identifiers, comments, log and error messages, commit messages.
- **Interface in European Portuguese**: every text the user sees in `web/`, written for Portugal (for example "ficheiro", "utilizador", "telemóvel").
- No em-dashes in the README, this file or the interface copy (the docs check enforces it).
- Never suggest that the project is official or connected to the tax authority. The word "oficial" refers only to a ruling and its PDF.
- Results are always passages quoted from cited rulings. Do not add text that reads as advice or as an answer not backed by a ruling.

## Pull requests

- Keep them small and focused, with a short description of what changed and why.
- Include the evidence listed above.
- By contributing you agree that your work is released under the [MIT licence](LICENSE).
