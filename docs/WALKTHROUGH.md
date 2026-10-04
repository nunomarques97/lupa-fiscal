# Walkthrough

How Lupa Fiscal works (v0.2: every tax with binding rulings, plus strict-citation answers from a local model), what was decided and what was left out. Current numbers and dated decisions: [STATE.md](STATE.md).

## Architecture

```
info.portaldasfinancas.gov.pt          data/ (local, never committed)
  13 listings (JSON) + PDFs     --->  corpus/<tax>/  manifest, pdf/, text/
        crawl, 1 req/s                       | index (merge identical PDFs)
                                             v
  models/multilingual-e5-small  --->  lupa-fiscal.db  rulings, listings, chunks, FTS5, vectors
                                             | load once
                                             v
  Angular UI (web/dist)  <---  minimal API on localhost:4401  <---  CLI: search, answer, eval, bench
                                             ^
  models/qwen3-4b (optional)  --->  answer generator (in process, CPU)
```

One solution, `LupaFiscal.slnx`:

- **LupaFiscal.Core**: crawling, PDF extraction, chunking, embeddings, index, hybrid search, answering and evaluation; everything else is a thin shell.
- **LupaFiscal.Cli**: `crawl`, `extract`, `corpus-status`, `model download`, `index`, `index-status`, `search`, `answer`, `eval`, `answer-eval`, `bench`.
- **LupaFiscal.Api**: `GET /api/search`, `/api/answer`, `/api/facets`, `/api/health`; serves the built UI.
- **web/**: Angular 21 search page in European Portuguese.
- **tests/**: xUnit on a synthetic corpus of two taxes with a fake embedder and a fake answer generator, so tests and CI never touch the network or a real model.

**Corpus.** The 13 libraries fill their tables with JavaScript, so the crawler calls the JSON listing service behind them ([research/sources.md](research/sources.md)): robots.txt first, requests at least 1 s apart, retries that back off and honour Retry-After, one allowed https host. Each PDF is cached once; a manifest per tax records each ruling's state and PDF hash, so a stopped crawl resumes.

**Extraction.** PdfPig reads the text layer. A PDF with fewer than 200 letters or digits counts as scanned and is listed, not indexed.

**One ruling, several taxes.** Byte-identical PDFs published in two libraries become one ruling, shown under the first tax in source order, while every listing stays in the index so each tax's filters still find it. CIRS ids never change, so the frozen eval set keeps working.

**Chunking.** Each ruling is cleaned of page banners and split into its sections: header, request, facts, legal framework, conclusion. Chunks never cross a section, hold at most 512 model tokens and repeat up to 64 tokens of the previous one.

**Embeddings.** `multilingual-e5-small` runs in ONNX Runtime on the CPU with its `query: ` and `passage: ` prefixes and mean pooling, pinned and checked by SHA-256 before each use.

**Search.** Two candidate lists of 100 chunks each: FTS5 BM25 (query reduced to quoted words, so user text is never FTS5 syntax) and brute-force cosine over all vectors in memory. Reciprocal rank fusion (k = 60) merges them at ruling level, one passage per ruling. Filters apply to both lists; the article filter requires a tax.

**Answers.** The same hybrid search gives the top 4 passages (at most 180 tokens each, prompt at most 1150 tokens), labelled `[1]` to `[4]` in rank order. Question and passages sit inside delimiters, stripped of angle and square brackets, so they cannot forge a citation or a template token. Qwen3-4B-Instruct (Apache-2.0, int4) runs in process with ONNX Runtime GenAI, greedy, at most 180 new tokens and 90 s. Code then splits the output into sentences and keeps only those whose citations all name a provided id; a sentence without one, with an unknown id, a source named another way ("(Fonte 2)") or the prompt's format example is removed. When nothing is left, the answer is one fixed refusal. The UI checks the ids again before showing anything.

**API and UI.** The API binds to loopback, accepts only local Host headers, validates every parameter and returns problem details on errors. The answer model loads in the background; until then, or without it, `/api/answer` returns 503 and search is unaffected. One generation runs at a time and stops when the client aborts. The UI shows the results first, then requests the answer; a new search abandons it, and a late response never replaces a newer one.

## Key decisions

- **Strict citations or nothing.** A generated sentence is shown only with a valid source, checked in code, never trusted to the prompt. No streaming: only complete, checked sentences appear.
- **SQLite for everything.** Rulings, listings, chunks, FTS5 and vectors in one file, with no server or extension.
- **Brute-force vectors.** 34,505 chunks of 384 dimensions is 50 MB; a full scan takes about 7 ms and hybrid p95 is 120 ms.
- **Hybrid with RRF.** Keywords catch exact legal terms; vectors catch everyday wording. On the full index hybrid ranks best (MRR@10 0.63 on the CIRS set against 0.56 for BM25 and 0.60 for vectors).
- **Frozen eval sets built before measuring.** Answers come from the subject lists, chosen without running any search; a hash stops edits after the fact. `answer-eval` only reads them.
- **Answer model chosen by measurement.** Qwen3-4B against Phi-4-mini (Brazilian Portuguese, weak answers) and EuroLLM-9B (ignored the citation format, slowest) on 12 questions ([research/answer-model.md](research/answer-model.md)).
- **Re-index only what changed.** A second `index` run takes under a minute.

## Rejected alternatives

- **Paid or hosted language models.** Better answers are likely, but they need an account, a key and money, and send every question away. The Sponsor chose local and free only.
- **Ollama, llama.cpp server or any model service.** A second process to install and keep running, and a model registry outside the project's control. Models run in process from pinned, verified files, for embeddings and answers alike.
- **Llama 3.x, Gemma, Qwen2.5-3B.** Custom licences with use restrictions.
- **PostgreSQL with pgvector.** A database server for a corpus that fits in memory.
- **iText for PDF text.** AGPL or a commercial licence; PdfPig is Apache-2.0.
- **sqlite-vec or an approximate index.** A native extension without stable releases, or recall traded for speed.
- **OCR.** Every ruling of the 13 taxes already has a text layer.
- **Larger embedding models (bge-m3).** Several times slower on a CPU; e5-small meets the target.

## Trade-offs

- **Answers are slow.** About 30 s on a free CPU (first word after about 22 s), up to the 90 s limit when other jobs share it; one at a time.
- **A citation proves provenance, not correctness.** In 1 of 12 measured answers the cited ruling does not support the conclusion. The UI presents the answer as a reading aid above the passages.
- **Memory.** The answer model adds about 3.6 GB to the 0.8 GB of search.
- **Some eval questions still miss.** Seven CIRS questions miss the top 10; an answer cannot cite what search did not find.
- **One source host.** If its listing service changes, the crawler must follow; the cached corpus keeps search working.
- **Windows-first.** Only Windows is tested in CI.
