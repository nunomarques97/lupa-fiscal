---
status: v0.2 part 1 done (local only, every tax with binding rulings, 13 libraries); v0.2 part 2 (answer synthesis) not started
sponsor_action: choose the model for v0.2 part 2 (answer synthesis with strict citations); any paid model is a Sponsor gate, as is public hosting
kill_review: passed 2026-10-02 (hybrid recall@10 0.90 on the 50-question eval, target 0.70); still met on the full multi-tax index 2026-10-03 (CIRS set 0.860, floor 0.85; other-taxes set 0.833, target 0.70)
success_metric: recall@10 >= 70 % and search under 1 s locally; on the full multi-tax index hybrid recall@10 0.860 (CIRS set) and 0.833 (other-taxes set), bench p95 120 ms
---

# State

## Current
v0.2 part 1 is complete and runs locally: crawl, extraction, index, search, API and UI cover all 13 binding-rulings libraries on the official host (CIRS, CIRC, DSRI, EBF, CIMI, CIMT, CIUC, SELO, CIVA, RITI, LGT, CESE, CSB). Evidence, detailed in the dated entries below:

- Corpus: 5998 listed rulings, 0 pending, 5998 downloaded and extracted, 0 scanned-skipped, 0 failed (`corpus-status --all`, exit 0; counts per tax in the 2026-10-02 crawl entry); 692 MB in `data/corpus/`.
- Index: 5790 distinct rulings (208 listings merged into the byte-identical PDF of an earlier tax), 34505 passages, all with vectors; `data/lupa-fiscal.db` is 215 MB (`index-status`, exit 0).
- Eval on the full index, hybrid: frozen CIRS set recall@10 0.860, MRR@10 0.632 (floor 0.85); new set of 24 questions over 7 other taxes recall@10 0.833, MRR@10 0.645 (target 0.70).
- Latency and memory: `bench` p50 77 ms, p95 120 ms, max 131 ms (limit 1 s); vector matrix 50.5 MB, process working set 739 to 790 MB after loading the model and vectors.
- Build times on a CPU: crawl about 107 minutes from scratch at 1 request per second (21 minutes for CIRS, 86 for the other 12 libraries); index about 38 minutes from scratch (283 s for CIRS, 33 min 41 s for the rest); a repeated `index` run takes 41 s and embeds nothing.
- UI: every tax in the tax filter, results across taxes with their tax shown, article select scoped to the chosen tax; 22 screenshots at 1440 and 390 px in [docs/evidence/](evidence/).
- Tests (2026-10-03, local): `dotnet test` 377 passed, `npm --prefix web test` 46 passed.

v0.1 (CIRS only) keeps working: the same 1189 CIRS rulings and ids, so the frozen CIRS eval set still applies. The repository is public (Sponsor decision, 2026-10-02). v0.2 part 2 (answer synthesis) has not started.

- 2026-10-02: v0.1 complete: 1189 CIRS rulings indexed as 5938 passages, hybrid recall@10 0.900 and MRR@10 0.631 (keyword only 0.800 and 0.578), search p50 32 ms, p95 40 ms, max 53 ms.

- 2026-10-02: Gate 0 passed. The CIRS listing (1189 rulings) and the ruling PDFs are publicly reachable on info.portaldasfinancas.gov.pt. robots.txt returns 404 (no restrictions under RFC 9309). The site terms allow reproduction with the source cited and for non-commercial use, and do not forbid automated download. Evidence: [docs/research/gate0.md](research/gate0.md), probe `node scripts/gate0-probe.mjs` (exit 0, 3 requests).
- 2026-10-02: CIRS corpus crawled and extracted. `corpus-status --tax CIRS` (exit 0): listed 1189, pending 0, downloaded 1189, extracted 1189, scanned-skipped 0, failed 0. Listing fetched 2026-10-02 13:52Z; the full crawl took 21 minutes (1189 requests at 1 per second, no 429 or 5xx responses). 2986 pages, about 90 MB of PDFs and 11 MB of text in `data/corpus/cirs/` (not committed). Every ruling has a process number (72 taken from the PDF text because the listing has none) and 1145 of 1189 have a decision date parsed from the text. 83 rulings concern another diploma (EBF, budget laws) and keep it in their `diploma` field.
- Scanned-PDF rule: a PDF whose text layer holds fewer than 200 letters or digits is listed in `data/corpus/cirs/scanned-skipped.md` and not indexed (no OCR in v0.1). The smallest CIRS ruling has 672, so none was skipped.
- 2026-10-02: CIRS index built in `data/lupa-fiscal.db` (38 MB, not committed). `index-status` (exit 0): 1189 extracted rulings, 1189 indexed, 0 missing, 0 outdated, 5938 chunks, 5938 with vectors. The first build took 283 s on CPU; running `index` again embeds nothing (1189 unchanged, same 5938 chunks). Chunks by section: header 1187, request 1134, facts 327, legal-framework 2676, conclusion 161, content 453 (rulings without headings); average 307 model tokens, maximum 512. `search "Posso deduzir as despesas de educação dos meus filhos no IRS?"` returns 10 education and dependants rulings (articles 13, 78-D, 83-A) in 154 ms, after 2.6 s to verify and load the model and vectors.
- Embedding model: `intfloat/multilingual-e5-small` (MIT licence), fp32 `onnx/model.onnx` with `onnx/sentencepiece.bpe.model` and `onnx/tokenizer.json`, pinned at revision `614241f622f53c4eeff9890bdc4f31cfecc418b3`. SHA-256: model.onnx `ca456c06b3a9505ddfd9131408916dd79290368331e7d76bb621f1cba6bc8665` (470,268,510 bytes), sentencepiece.bpe.model `cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865`, tokenizer.json `0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39`. Downloaded without an account into `data/models/multilingual-e5-small/` (not committed); every file is checked against its SHA-256 before use.
- 2026-10-02: retrieval eval. 50 plain-Portuguese questions over 29 CIRS articles, each with the rulings that answer it (197 in total), frozen before measuring: `eval/questions.json`, method and coverage in `eval/README.md`. Results at ruling level (`eval`, exit 0 with `--min-recall 0.70`):

  | Mode | recall@10 | MRR@10 |
  |---|---|---|
  | keyword only (BM25) | 0.800 | 0.578 |
  | vector only | 0.860 | 0.613 |
  | hybrid (RRF) | 0.900 | 0.631 |

  Hybrid answers 45 of 50 questions in the top 10; the 5 misses are listed in [docs/eval/report.md](eval/report.md). The baseline parameters already meet the 0.70 target, so no tuning iteration was run.
- 2026-10-02: latency (`bench`, 50 hybrid queries after one warm-up, query embedding included, CPU, recorded with `--record`): p50 32 ms, p95 40 ms, max 53 ms, against the 1 s limit. Loading the model and vectors once takes about 1.9 s.
- 2026-10-02: local API (`dotnet run --project src/LupaFiscal.Api`) on the real CIRS index. It binds to 127.0.0.1:4401 and [::1]:4401 only (netstat), also when started with `--urls http://0.0.0.0:4402`, which Kestrel overrides. Startup loads the model, 1189 rulings and 5938 chunks. Timed request at 2026-10-02T15:12:50Z: `curl -w %{time_total} "http://localhost:4401/api/search?q=Posso deduzir as despesas de educação dos meus filhos no IRS?"` returned 200 with 10 results in 0.110 s end to end (`tookMs` 85.1, first query after startup); the same request again took 0.040 s (`tookMs` 37). Limit 1 s.
- 2026-10-02: design. Three directions rendered with real passages from the index (`docs/ui/`, `npm run ui:directions`): A citation register, B accountant console, C question and evidence. A confirmed, B and C rejected with the reasons in [DESIGN.md](../DESIGN.md).
- 2026-10-02: Angular UI (`web/`) built on DESIGN.md and served by the API on http://localhost:4401. `npm run ui:evidence:publish` drove the real UI on the real index with the installed Playwright Chromium and saved 15 screenshots at 1440 and 390 px in [docs/evidence/](evidence/): initial, validation, loading, results, filters (plus the mobile filter sheet), empty and error. It also asserts no horizontal overflow, keyboard-only use, URL restore, that a late earlier response (success or failure) never replaces a newer one, and retry of the last search.
- 2026-10-02: open-source packaging. README (screenshots, build and run, model and third-party licences), LICENSE (MIT), CONTRIBUTING, [WALKTHROUGH](WALKTHROUGH.md) and CI (`.github/workflows/ci.yml`: Windows, .NET build and tests on the synthetic corpus with the fake embedder, web build and tests, docs checks; no crawl, no model download, no secrets). `node scripts/validate-docs.mjs` checks this file's front-matter, em-dashes in the copy, the WALKTHROUGH length, README images, the licence and credential-like file names. Local runs: `dotnet test` 307 passed, `npm --prefix web test` 29 passed, docs checks 9 passed. CI passed on GitHub after the first push.
- 2026-10-02: v0.2 source discovery passed. The landing page links 13 binding-rulings libraries (CIRS, CIRC, DSRI, EBF, CIMI, CIMT, CIUC, SELO, CIVA, RITI, LGT, CESE, CSB), each listable with one unpaged `listdocs` call: 5998 rulings and 5998 PDF URLs in total (CIVA 2993, CIRS 1189, CIRC 859, EBF 245, CIMT 221, SELO 200, DSRI 120, CIMI 96, RITI 43, CIUC 21, LGT 6, CESE 3, CSB 2); 219 file names appear in two libraries. None is unsupported. The CIMT and CIUC numbered views are filtered, so the crawler should send the unfiltered filter (CIMT then lists 221 instead of 188). Estimated crawl of the 4809 new PDFs: about 85 minutes at 1 request per second. Evidence: [docs/research/sources.md](research/sources.md), `node scripts/sources-probe.mjs` (two runs of 60 requests, robots.txt 404, every other request 200), offline tests `node --test scripts/sources-probe.test.mjs`.
- 2026-10-02: full corpus crawled and extracted for all 13 libraries. `crawl --all` ran as one independent background process from 21:20:15Z to 22:46:17Z (86 minutes wall time, logging to each `data/corpus/<tax>/crawl.log`): 4835 requests (13 robots.txt, 13 listings, 4809 new PDFs; CIRS needed none), interval 1 s, no retry. A second pass `crawl --all --retry-failed --use-cached-listing` had nothing to retry (13 robots.txt requests, 0 downloads). Over both runs: 4848 requests, 4822 answered 200, 26 robots.txt 404 (no restrictions), no 429 and no 5xx. `corpus-status --all` (exit 0): 5998 listed, 0 pending, 5998 downloaded and extracted, 0 scanned-skipped, 0 failed, so `--list-failed` lists no reason. Per tax (listing fetch time UTC, requests in the main run, PDF size):

  | Tax | Listed | Downloaded | Extracted | Scanned-skipped | Failed | Listing fetched | Requests | PDFs |
  |---|---|---|---|---|---|---|---|---|
  | CIRS | 1189 | 1189 | 1189 | 0 | 0 | 21:20:17 | 2 | 90.0 MB |
  | CIRC | 859 | 859 | 859 | 0 | 0 | 21:20:19 | 861 | 90.9 MB |
  | DSRI | 120 | 120 | 120 | 0 | 0 | 21:35:38 | 122 | 9.1 MB |
  | EBF | 245 | 245 | 245 | 0 | 0 | 21:37:48 | 247 | 23.7 MB |
  | CIMI | 96 | 96 | 96 | 0 | 0 | 21:42:10 | 98 | 12.8 MB |
  | CIMT | 221 | 221 | 221 | 0 | 0 | 21:43:54 | 223 | 25.0 MB |
  | CIUC | 21 | 21 | 21 | 0 | 0 | 21:48:09 | 23 | 2.7 MB |
  | SELO | 200 | 200 | 200 | 0 | 0 | 21:48:33 | 202 | 26.0 MB |
  | CIVA | 2993 | 2993 | 2993 | 0 | 0 | 21:52:11 | 2995 | 347.1 MB |
  | RITI | 43 | 43 | 43 | 0 | 0 | 22:45:13 | 45 | 5.0 MB |
  | LGT | 6 | 6 | 6 | 0 | 0 | 22:46:01 | 8 | 0.4 MB |
  | CESE | 3 | 3 | 3 | 0 | 0 | 22:46:09 | 5 | 0.5 MB |
  | CSB | 2 | 2 | 2 | 0 | 0 | 22:46:15 | 4 | 0.7 MB |
  | All | 5998 | 5998 | 5998 | 0 | 0 | | 4835 | 633.8 MB |

  Disk use in `data/corpus/` (file sizes, not committed): 692 MB, of which 634 MB PDFs and 49 MB text; 18198 pages. The smallest text layer has 424 letters or digits (CIRC), above the 200 scanned threshold. CIRS is unchanged: the same 1189 rulings with identical metadata and PDF hashes; only the listing fetch time moved. The 5998 listed rulings hold 5787 distinct PDF hashes; 208 hashes appear in more than one library (merged by the index, not by the crawler).
- 2026-10-02: full multi-tax index built in `data/lupa-fiscal.db` (215 MB, not committed). The v0.1 CIRS index (schema 1) was migrated in place, so its 1189 rulings were not embedded again. `index` ran as one background process from 23:10:08Z to 23:43:47Z (33 min 41 s wall time on CPU, shared with an Android emulator running on the same machine): 5998 listings, 208 merged into the byte-identical PDF of an earlier tax, 5790 distinct rulings; embedded 4601 rulings (28567 passages), unchanged 1189, removed 0. A second `index` run took 41 s and embedded nothing (5790 unchanged). `index-status` (exit 0): every tax complete, 0 missing, 0 outdated, 0 stale, every passage has a vector. Per tax (a merged ruling is shown under the first tax in source order that lists it, and stays findable through every listing):

  | Tax | Listed | Merged into an earlier tax | Distinct rulings | Passages |
  |---|---|---|---|---|
  | CIRS | 1189 | 0 | 1189 | 5938 |
  | CIRC | 859 | 2 | 857 | 3585 |
  | DSRI | 120 | 0 | 120 | 510 |
  | EBF | 245 | 172 | 73 | 464 |
  | CIMI | 96 | 15 | 81 | 448 |
  | CIMT | 221 | 17 | 204 | 1361 |
  | CIUC | 21 | 0 | 21 | 131 |
  | SELO | 200 | 2 | 198 | 1479 |
  | CIVA | 2993 | 0 | 2993 | 20183 |
  | RITI | 43 | 0 | 43 | 348 |
  | LGT | 6 | 0 | 6 | 29 |
  | CESE | 3 | 0 | 3 | 19 |
  | CSB | 2 | 0 | 2 | 10 |
  | All | 5998 | 208 | 5790 | 34505 |

- 2026-10-02: vector scan measured on the full index. 34505 vectors of 384 float32 make a 50.5 MB matrix; after loading the model and vectors the `bench` process has a working set of 739 to 790 MB (most of it the 470 MB fp32 model) and a managed heap of 83 MB; loading takes 2.3 to 3.8 s. `bench --questions eval/questions.json --max-ms 1000` (50 hybrid queries, exit 0) in three runs: p50 77 to 151 ms, p95 120 to 299 ms, max 131 to 530 ms (the two slower runs shared the CPU with the emulator); the run recorded with `--record` in [docs/eval/report.md](eval/report.md): p50 77 ms, p95 120 ms, max 131 ms, against 32, 40 and 53 ms for v0.1 (CIRS only). Split per mode (Release build, 50 queries each, throwaway harness not committed): query embedding p50 11 ms, vector only p50 18 ms (p95 44), keyword only p50 60 ms (p95 112), hybrid p50 88 ms (p95 141). The exact scan itself costs about 7 ms; the growth comes mostly from BM25 over an FTS table six times larger. p95 stays far under 1 s, so the brute-force scan is kept and no alternative was assessed or adopted.
- 2026-10-03: retrieval eval on the full multi-tax index (5790 rulings), both sets recorded with `eval --record`. `eval` now writes the report and history only with `--record`, like `bench`; a plain run prints the scores, exits by `--min-recall` and writes nothing (unit test, and both check commands left `docs/eval/` byte-identical). New set `eval/questions-taxes.json`: 24 plain-Portuguese questions over 7 non-CIRS taxes (CIVA 8, CIRC 5, CIMT 3, SELO 3, CIMI 2, EBF 2, CIUC 1) with 90 expected rulings chosen from the subject lists without running any search, frozen with `eval --freeze` (hash `29cdcc1b6214968d`, `docs/eval/taxes/history.json`) before its first measurement; method and coverage in [eval/README.md](../eval/README.md). Results at ruling level, same retrieval parameters as v0.1 (no tuning iteration was needed on either set):

  | Set | Mode | recall@10 | MRR@10 |
  |---|---|---|---|
  | CIRS (50 questions) | keyword only | 0.740 | 0.562 |
  | CIRS | vector only | 0.860 | 0.595 |
  | CIRS | hybrid | 0.860 | 0.632 |
  | Other taxes (24 questions) | keyword only | 0.708 | 0.484 |
  | Other taxes | vector only | 0.833 | 0.598 |
  | Other taxes | hybrid | 0.833 | 0.645 |

  CIRS hybrid recall@10 went from 0.900 on the CIRS-only index to 0.860 (floor 0.85, `--min-recall 0.85` exits 0); 7 misses, listed in [docs/eval/report.md](eval/report.md), whose history keeps the v0.1 row. Other taxes: hybrid 0.833 against the 0.70 target, 4 misses (t04 tutoring, t10 electric car in IRC, t18 cash gift, t22 IMI exemption) in [docs/eval/taxes/report.md](eval/taxes/report.md).
- 2026-10-03: UI on the full multi-tax index, following DESIGN.md without a redesign. The tax select lists the 13 indexed taxes with their labels and counts from `/api/facets`; the article select stays disabled with a hint until a tax is chosen and then lists only that tax's articles; clearing the tax clears the article; every result shows its tax (IRS, IVA, IMT and so on). `npm run ui:evidence:publish` drove the real UI on the real index with the installed Playwright Chromium and wrote 22 screenshots at 1440 and 390 px to [docs/evidence/](evidence/): the v0.1 states plus `taxes` (one question answered from several taxes), `taxes-results` (the first results up to the third distinct tax), `tax-filter` and the mobile `tax-filter-sheet` (IMT, article 2.º). Its assertions cover results from at least 3 taxes matching the API, keyboard-only choice of tax and article with focus kept, the tax and article in the URL, a late earlier response never replacing the filtered one, and clearing the tax.

## Next
1. v0.2 part 2: answer synthesis with strict citations (every statement linked to the ruling passage it comes from, nothing without a source). Waits for a Sponsor model decision: a free local model, or a paid model, which is a Sponsor gate.
2. Keep the corpus current: rerun `crawl --all` and `index` from time to time; only new or changed rulings are downloaded and embedded. Measure the vector scan again with `bench` if the corpus grows well beyond 34505 passages.
3. Before public hosting (v1): confirm that showing passages publicly stays within the site terms (source cited, non-commercial). See docs/research/gate0.md.

Done since the last update: the repository was made public on 2026-10-02 (Sponsor decision) and CI runs on every push.

## Roadmap
- v0.1 (done 2026-10-02): local semantic search over CIRS rulings, no generated answers.
- v0.2 part 1 (done 2026-10-03): every tax with binding rulings (13 libraries), still no generated answers.
- v0.2 part 2: answer synthesis with strict citations: every statement linked to the ruling passage it comes from, nothing without a source. Any paid model is a Sponsor gate.
- v1: public hosting (Sponsor gate).

## Decisions
- 2026-10-02: no Linear; priorities and roadmap live here.
- 2026-10-02: repository made public by Sponsor decision (it started private).
- 2026-10-02: v0.2 listings: every library is crawled with its chosen page item id, the unfiltered CAML and the superset field list recorded per library in docs/research/sources.md, because some numbered views are filtered and field names differ per library.
- 2026-10-02: Gate 0 passed; crawl only https://info.portaldasfinancas.gov.pt, at most 1 request per second, using the listing endpoint and field mapping in docs/research/gate0.md.
- 2026-10-02: corpus cache layout `data/corpus/<tax>/` with `manifest.json` (metadata and state per ruling), `listing.json`, `pdf/<id>.pdf`, `text/<id>.txt`, `scanned-skipped.md` and `crawl.log`. Ruling ids are sanitised PDF file names (`[a-z0-9_-]`). A run stops after 3 downloads in a row fail after their retries, leaving those rulings pending.
- 2026-10-02: index design. Each ruling body is cleaned of page furniture (page banners, "1Processo: N" footers) and split into sections: header (up to "Conteúdo:"), request, facts, legal-framework (INFORMAÇÃO, PARECER, ENQUADRAMENTO, ANÁLISE), conclusion, or content when no heading is found. Chunks never cross a section and hold at most 512 model tokens including the "passage: " prefix and special tokens. Overlap: the next chunk repeats the last whole sentences of the previous one, up to 64 tokens; sentences longer than 64 tokens are cut at word boundaries. Chunk offsets refer to the cleaned body stored in `rulings.body`.
- 2026-10-02: search design. Keyword list: FTS5 (unicode61, diacritics removed) BM25 over chunk text; the query is reduced to words, Portuguese stop words dropped, each word quoted and joined with OR, so user text is never FTS5 syntax. Vector list: brute-force cosine over all chunk vectors in memory. Each list takes 100 candidates; reciprocal rank fusion with k = 60 at ruling level, one passage per ruling (its best fused chunk). Filters (tax, article, year) apply to both lists; the year is the publication year from the listing (always present; the decision date is missing for 44 rulings). A ruling is re-embedded only when its cleaned text, the chunking settings or the model change, or a chunk lacks a vector.
- 2026-10-02: v0.2 vector search stays the exact in-memory scan. On the full index (34505 vectors, 50.5 MB) the scan takes about 7 ms and hybrid p95 is 120 ms, far under the 1 s limit, so no approximate index or extension is needed. `bench` prints the matrix size and process working set after loading so the next corpus growth can be checked the same way.
- 2026-10-02: API contract. `GET /api/search` validates q (required, not blank, at most 500 characters), year (digits, 1900 to 2100) and limit (digits, 1 to 50, default 10, never clamped); each parameter at most once; an empty year or limit means absent, blank tax or article means no filter, an unknown tax or article returns 200 with no results. Errors are problem-details (400 validation, 404 for unknown `/api` paths, 500 with no exception detail). Results are plain text with highlights as UTF-16 `{start, length}` offsets into the passage; a result whose source URL is not https on the official host is dropped. Only Host headers localhost, 127.0.0.1 and [::1] are accepted. Facets count rulings with chunks per tax, article (numeric order) and publication year (newest first). Amended for v0.2 by the tax-scoped article filter below.
- 2026-10-02: v0.2 cross-tax dedup. A ruling is identified by its PDF content (SHA-256): byte-identical PDFs listed by several taxes become one ruling and one result. It keeps the id and display tax of its first listing in source order (CIRS, CIRC, DSRI, EBF, CIMI, CIMT, CIUC, SELO, CIVA, RITI, LGT, CESE, CSB), and every listing it appears in stays in the index, so the tax and article filters of each listing still find it. The same file name with different content stays two rulings with distinct ids, and entries within one tax are kept as published. The crawler only records hashes; the merge happens in the index, so CIRS ids never change and the frozen CIRS eval set keeps working. The v0.1 index was migrated in place, without embedding CIRS again.
- 2026-10-02: v0.2 tax-scoped article filter and facets contract. Article numbers are only meaningful within one tax code, so an article without a tax is rejected: `GET /api/search` returns a 400 problem and `search --article` without `--tax` exits 2. `GET /api/facets` returns `taxes: [{value, count}]`, `articles: [{tax, value, count}]` and `years: [{value, count}]`, counting distinct rulings per listing, so a merged ruling counts under each tax that lists it. The UI keeps the article select disabled until a tax is chosen and lists only that tax's articles. `crawl`, `extract` and `corpus-status` take `--all` (every supported tax, one after another through one polite client); `index` and `index-status` without `--tax` cover every tax.
- 2026-10-03: eval records only on request. `eval` and `bench` print their numbers and write `docs/eval/` only with `--record`, so checks never change tracked files; `eval --freeze` pins a new question set's hash before its first measurement, and any run (recorded or not) refuses a set that differs from its frozen hash. The eval configuration includes the index size, so scores on a grown index are a new row and earlier rows stay. The multi-tax set lives in `eval/questions-taxes.json` with its report and history in `docs/eval/taxes/`; its `tax` field is a report label outside the hash.
- 2026-10-02: eval metrics. recall@10 counts a question as answered when any of its expected rulings (alternative answers) is in the first 10 distinct rulings; MRR@10 uses the first one. Passages of one ruling count once. The question set is frozen by a hash in `docs/eval/history.json`; `eval` refuses to record an iteration against an edited set.
- 2026-10-02: README and CONTRIBUTING are in English for an open-source audience; the UI stays in European Portuguese. Both are drafts for the Sponsor: no em-dashes, nothing about how the project was built, no suggestion of a link with the tax authority beyond the independence statement. CI is a single Windows job with read-only permissions and no secrets.
