# Walkthrough

How Lupa Fiscal v0.1 works, what was decided and what was left out. Current numbers and dated decisions: [STATE.md](STATE.md).

## Architecture

```
info.portaldasfinancas.gov.pt          data/ (local, never committed)
  listing (JSON) + ruling PDFs  --->  corpus/cirs/  manifest, pdf/, text/
        crawl, 1 req/s                       | index
                                             v
  models/multilingual-e5-small  --->  lupa-fiscal.db  rulings, chunks, FTS5, vectors
                                             | load once
                                             v
  Angular UI (web/dist)  <---  minimal API on localhost:4401  <---  CLI: search, eval, bench
```

One solution, `LupaFiscal.slnx`:

- **LupaFiscal.Core**: crawling, PDF extraction, chunking, embeddings, index, hybrid search and evaluation. Everything else is a thin shell around it.
- **LupaFiscal.Cli**: `crawl`, `extract`, `corpus-status`, `model download`, `index`, `index-status`, `search`, `eval`, `bench`.
- **LupaFiscal.Api**: `GET /api/search`, `/api/facets`, `/api/health`; serves the built UI.
- **web/**: Angular 21 search page in European Portuguese.
- **tests/**: xUnit on a synthetic corpus (PDFs generated in the tests, listings modelled on the real format) with a fake embedder, so tests and CI never touch the network or the real model.

**Corpus.** The CIRS list pages fill their tables with JavaScript, so the crawler calls the JSON listing service those pages use (documented in [research/gate0.md](research/gate0.md)). robots.txt is read first on every run. Requests are at least 1 s apart, retries back off and honour Retry-After, only one https host is allowed, and every PDF is cached once in `data/corpus/`. A manifest records each ruling's state, so a stopped crawl resumes where it ended.

**Extraction.** PdfPig reads the text layer. A PDF with fewer than 200 letters or digits counts as scanned and is listed, not indexed. Metadata (tax, article, dates, process number, URL) comes from the listing, with the process number and decision date read from the text when the listing lacks them.

**Chunking.** Each ruling is cleaned of page banners and footers and split into its sections: header, request, facts, legal framework, conclusion. Chunks never cross a section, hold at most 512 model tokens and repeat up to 64 tokens of whole sentences from the previous chunk.

**Embeddings.** `multilingual-e5-small` runs in ONNX Runtime on the CPU with its `query: ` and `passage: ` prefixes and mean pooling. The SentencePiece tokeniser comes from Microsoft.ML.Tokenizers with the XLM-R id mapping; a test compares its output with the model's `tokenizer.json`. The model is downloaded at a pinned revision and checked by SHA-256 before each use.

**Search.** Two candidate lists of 100 chunks each: FTS5 BM25 (diacritics removed, query reduced to quoted words joined with OR, so user text is never FTS5 syntax) and brute-force cosine over all vectors held in memory. Reciprocal rank fusion (k = 60) merges them at ruling level, one passage per ruling. Filters apply to both lists. Highlights are character offsets, never HTML.

**API and UI.** The API binds to loopback only and accepts only local Host headers. It validates every parameter and returns problem details on errors. The UI keeps the query in the URL, cancels outdated requests and never lets a late response replace a newer one. Every passage is shown with its citation and a link to the official PDF.

## Key decisions

- **No generated answers in v0.1.** Every result is a quoted passage from a cited ruling, so nothing can be invented. This is the product promise and the design rule: the citation is never hidden.
- **SQLite for everything.** Rulings, chunks, FTS5 and vectors (float32 BLOBs) sit in one file. No server and no extension to install.
- **Brute-force vectors.** About 6,000 chunks of 384 dimensions is 9 MB; a full scan takes a few milliseconds. Exact results, no index to tune.
- **Hybrid with RRF.** Keyword search catches exact legal terms ("mais-valias", article numbers); vectors catch everyday wording. RRF needs no score calibration. On the 50-question eval it beat both: recall@10 0.90 against 0.80 for BM25 and 0.86 for vectors.
- **Frozen eval set built before measuring.** Questions were written from the rulings' facts, and answers chosen from the subject list without running any search, so the set favours no mode. A hash stops it from being edited after the fact.
- **Re-index only what changed.** A ruling is embedded again only when its cleaned text, the chunking settings or the model change, so a second `index` run takes seconds.

## Rejected alternatives

- **PostgreSQL with pgvector.** A database server to install and run on Windows, for a corpus that fits in memory. It would make "clone and run" much harder for no gain at this size.
- **Ollama for embeddings.** A separate service and a model registry outside the project's control. ONNX Runtime runs the same kind of model inside the process, with a pinned and verified file.
- **iText for PDF text.** AGPL or a commercial licence, which conflicts with an MIT project at zero cost. PdfPig is Apache-2.0 and fully managed.
- **sqlite-vec.** A native extension without stable releases, to load on every platform. At CIRS scale it brings no measurable speed over a plain scan.
- **OCR in v0.1.** It adds a heavy dependency and recognition errors to a corpus where every CIRS ruling already has a text layer (none was skipped). Scanned PDFs are listed so the gap stays visible.
- **Larger models (bge-m3).** Better on paper but several times slower on a CPU and much larger to download. e5-small already meets the target; bge-m3 stays the fallback.

## Trade-offs

- **Memory grows with the corpus.** Vectors live in RAM (about 1.5 KB per chunk). Fine for CIRS; with every tax in v0.2 it is still tens of MB, but an approximate index may be worth measuring then.
- **Startup cost.** Verifying and loading the model and vectors takes about 2 s; each search then takes about 30 ms.
- **Section detection relies on headings.** Rulings without the usual headings (453 chunks) fall back to plain content chunks.
- **Five eval questions still miss.** They need reasoning across rulings (losses on one asset against gains on another) or match only generic wording. Generated answers could help in v0.2, but only with strict citations.
- **One source host.** If the tax authority changes its listing service, the crawler must follow; the cached corpus keeps search working meanwhile.
- **Windows-first.** The code is portable .NET and Node, but only Windows is tested and checked in CI.
