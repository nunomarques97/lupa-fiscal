# Walkthrough

How Lupa Fiscal works (v0.2 part 1: every tax with binding rulings, no generated answers), what was decided and what was left out. Current numbers and dated decisions: [STATE.md](STATE.md).

## Architecture

```
info.portaldasfinancas.gov.pt          data/ (local, never committed)
  13 listings (JSON) + PDFs     --->  corpus/<tax>/  manifest, pdf/, text/
        crawl, 1 req/s                       | index (merge identical PDFs)
                                             v
  models/multilingual-e5-small  --->  lupa-fiscal.db  rulings, listings, chunks, FTS5, vectors
                                             | load once
                                             v
  Angular UI (web/dist)  <---  minimal API on localhost:4401  <---  CLI: search, eval, bench
```

One solution, `LupaFiscal.slnx`:

- **LupaFiscal.Core**: crawling, PDF extraction, chunking, embeddings, index, hybrid search and evaluation; everything else is a thin shell.
- **LupaFiscal.Cli**: `crawl`, `extract`, `corpus-status`, `model download`, `index`, `index-status`, `search`, `eval`, `bench`.
- **LupaFiscal.Api**: `GET /api/search`, `/api/facets`, `/api/health`; serves the built UI.
- **web/**: Angular 21 search page in European Portuguese.
- **tests/**: xUnit on a synthetic corpus of two taxes (PDFs generated in the tests, listings modelled on the real format) with a fake embedder, so tests and CI never touch the network or the real model.

**Corpus.** The official site has 13 libraries of binding rulings. Their list pages fill their tables with JavaScript, so the crawler calls the JSON listing service behind them, with each library's item id, unfiltered query and field names ([research/sources.md](research/sources.md)). `--all` crawls the libraries in turn through one polite client: robots.txt first, requests at least 1 s apart, retries that back off and honour Retry-After, one allowed https host. Each PDF is cached once in `data/corpus/<tax>/`; a manifest per tax records each ruling's state and PDF hash, so a stopped crawl resumes.

**Extraction.** PdfPig reads the text layer. A PDF with fewer than 200 letters or digits counts as scanned and is listed, not indexed. Metadata comes from the listing; a missing process number or decision date is read from the text.

**One ruling, several taxes.** Some rulings are published in two libraries (172 of the 208 duplicates are EBF entries). Identity is the PDF content: byte-identical PDFs become one ruling, shown under the first tax in source order (CIRS first), while every listing stays in the index so each tax's filters still find it. Same file name with different content stays two rulings. CIRS ids never change, so the frozen eval set keeps working.

**Chunking.** Each ruling is cleaned of page banners and footers and split into its sections: header, request, facts, legal framework, conclusion. Chunks never cross a section, hold at most 512 model tokens and repeat up to 64 tokens of whole sentences from the previous chunk.

**Embeddings.** `multilingual-e5-small` runs in ONNX Runtime on the CPU with its `query: ` and `passage: ` prefixes and mean pooling. The SentencePiece tokeniser comes from Microsoft.ML.Tokenizers, tested against the model's `tokenizer.json`. The model is pinned and checked by SHA-256 before each use.

**Search.** Two candidate lists of 100 chunks each: FTS5 BM25 (diacritics removed, query reduced to quoted words joined with OR, so user text is never FTS5 syntax) and brute-force cosine over all vectors held in memory. Reciprocal rank fusion (k = 60) merges them at ruling level, one passage per ruling. Filters apply to both lists. An article is only meaningful within its tax code, so the article filter requires a tax. Highlights are character offsets, never HTML.

**API and UI.** The API binds to loopback, accepts only local Host headers, validates every parameter and returns problem details on errors. Facets list articles per tax. The UI enables the article select only once a tax is chosen, keeps the query in the URL, cancels outdated requests and never lets a late response replace a newer one. Every passage is shown with its tax, citation and a link to the official PDF.

## Key decisions

- **No generated answers yet.** Every result is a quoted passage from a cited ruling, so nothing can be invented. This is the product promise and the design rule: the citation is never hidden.
- **SQLite for everything.** Rulings, listings, chunks, FTS5 and vectors (float32 BLOBs) in one file, with no server or extension. The v0.1 index was migrated in place, so CIRS was not embedded again.
- **Brute-force vectors.** 34,505 chunks of 384 dimensions is 50 MB; a full scan takes about 7 ms and hybrid p95 is 120 ms. Exact results, no index to tune.
- **Hybrid with RRF.** Keyword search catches exact legal terms ("mais-valias", article numbers); vectors catch everyday wording. RRF needs no score calibration. On the full index hybrid ranks best (MRR@10 0.63 on the CIRS set against 0.56 for BM25 and 0.60 for vectors).
- **Frozen eval sets built before measuring.** Questions come from the rulings' facts and answers from the subject lists, chosen without running any search. A hash stops edits after the fact. A second set covers seven other taxes. Scores are written only with `--record`.
- **Re-index only what changed.** A ruling is embedded again only when its cleaned text, the chunking settings or the model change, so a second `index` run takes under a minute.

## Rejected alternatives

- **PostgreSQL with pgvector.** A database server to run on Windows for a corpus that fits in memory: a harder "clone and run" for no gain.
- **Ollama for embeddings.** A separate service and a model registry outside the project's control. ONNX Runtime runs the model in process from a pinned, verified file.
- **iText for PDF text.** AGPL or a commercial licence; PdfPig is Apache-2.0 and fully managed.
- **sqlite-vec or an approximate index.** A native extension without stable releases, or recall traded for speed. The exact scan is a small part of the latency.
- **OCR.** It adds a heavy dependency and recognition errors to a corpus where every ruling of the 13 taxes already has a text layer. Scanned PDFs are listed so the gap stays visible.
- **Larger models (bge-m3).** Several times slower on a CPU and larger to download; e5-small meets the target, bge-m3 stays the fallback.

## Trade-offs

- **Memory grows with the corpus.** Vectors live in RAM (about 1.5 KB per chunk, 50 MB today); most of the process memory is the model.
- **Startup cost.** Loading the model and vectors takes about 3 s; a search then takes about 100 ms, mostly BM25.
- **Section detection relies on headings.** Without them, a ruling gets plain content chunks.
- **Some eval questions still miss.** Seven CIRS questions miss the top 10 (0.86 against 0.90 on CIRS alone), most ranked just below it. Generated answers could help, but only with strict citations.
- **One source host.** If its listing service changes, the crawler must follow; the cached corpus keeps search working.
- **Windows-first.** The code is portable .NET and Node, but only Windows is tested in CI.
