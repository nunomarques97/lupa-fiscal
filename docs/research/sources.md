# Sources: every binding-rulings library

Date: 2026-10-02. Outcome: **passed**. The official landing page links 13 libraries of binding rulings, and every one of them can be listed with the same `listdocs` endpoint that v0.1 uses for CIRS: one unpaged JSON response per library, PDFs on the same host. robots.txt still returns 404 (no restrictions). Together the 13 listings hold **5998 rulings with 5998 distinct PDF URLs**; 219 file names appear in two libraries. No library is unsupported.

Evidence: `node scripts/sources-probe.mjs`, run twice on 2026-10-02 with the User-Agent `LupaFiscal/0.2 (+https://github.com/nunomarques97/lupa-fiscal; open-source research crawler)`, 1.5 s between requests, redirects not followed, a budget of 60 requests per run, robots.txt first (request logs at the end):

- Run 1, 20:47:24Z to 20:49:04Z, 60 requests, all answered (59 x 200, robots.txt 404): every library's chosen list page and listing, other view pages, one 5-byte PDF sample per library.
- Run 2, 20:53:04Z to 20:54:46Z, 60 requests, all answered (59 x 200, robots.txt 404), `--skip-samples`: the unfiltered superset listing per library with every field its views use, and three comparison listings.

Before run 1, 5 single requests with the same User-Agent mapped the page structure (about 20:41Z): `/robots.txt` 404, `/pt/informacao_fiscal/informacoes_vinculativas/` 302 to the same host's `Pages/default.aspx` (not followed), that page 200, the CIRC numbered view 200, and the script `/_layouts/15/portalat/js/listdocs.js` 200. In total 125 requests. No ruling, listing or page is in the repository: raw responses, `requests.json` and `summary.json` stay in the probe's scratch directory (`--scratch`, default `<os tmp>/lupa-fiscal-sources-probe`), and the probe refuses a directory inside the repository.

## How the probe works

`scripts/sources-probe.mjs` (offline tests: `node --test scripts/sources-probe.test.mjs`):

1. Fetches `/robots.txt` and obeys it for every later URL (404 means no restrictions; 5xx or a disallowed landing page stops the run).
2. Parses the landing page `/pt/informacao_fiscal/informacoes_vinculativas/Pages/default.aspx`: an accordion per category (Rendimento, Benefícios Fiscais, Património, Despesa, Justiça Tributária, Contribuições Extraordinárias), an `<h4>` per library followed by its view links. LGT, CESE and CSB are single links without a heading.
3. Fetches the chosen view of each library (the numbered view, else the single page, else the date view) and reads the `listdocs.js` page variables `__fieldnames`, `__listsort`, `__filtervalue`, `__itemid`, `__enableSearch`, `__libraries` and the web path `_spPageContextInfo.webServerRelativeUrl`. Links on these pages are parsed too: they lead only to the five category pages, which have no listing, and to library home pages.
4. Fetches other views' variables (most informative first), then one superset listing per library and comparison listings.

The listing URL is built exactly as `listdocs.js` does: `<web>/_vti_bin/portalat/docs.svc/listdocs?fields=<__fieldnames>&sort=<__listsort>&filter=<URL-encoded __filtervalue>&id=<__itemid>` (plus `&library=` when `__libraries` is set; no library sets it). Every library has `__enableSearch = 0`, so the paged `searchdocs` variant is never used. The response is `{"data": [[...], ...], "total": 0}` with one row per entry and columns in field order; `total` is always 0, count `data.length`.

## Libraries

Web paths are relative to `/pt/informacao_fiscal/informacoes_vinculativas`. Entries and distinct PDFs are from the superset listing (run 2). The page filter is the one the chosen view sends; "unfiltered" is `<IsNotNull><FieldRef Name="ID"></FieldRef></IsNotNull>`.

| Code | Library (category) | Web path | Chosen view page | Item id | Page filter | Entries | Distinct PDFs | Published | Differences from CIRS |
|---|---|---|---|---|---|---|---|---|---|
| CIRS | CIRS (Rendimento) | `/rendimento/cirs` | `Pages/Vinc_numero.aspx` | 42 | unfiltered | 1189 | 1189 | 2006-08-24 to 2026-09-23 | reference |
| CIRC | CIRC (Rendimento) | `/rendimento/circ` | `Pages/Vinc_numero.aspx` | 43 | unfiltered | 859 | 859 | 2006-11-23 to 2026-09-23 (2 undated) | same fields; `Anterior_x0020_Artigo` filled for 102 |
| DSRI | Relações Internacionais (Rendimento) | `/rendimento/DSRI` | `Pages/Vinc_numero.aspx` | 27 | unfiltered | 120 | 120 | 2008-07-24 to 2026-08-20 | same fields; `Diploma` is mostly a tax treaty (37 diplomas) |
| EBF | Estatuto dos Benefícios Fiscais (Benefícios Fiscais) | `/beneficios_fiscais` | `Pages/Vinc_numero.aspx` | 35 | unfiltered | 245 | 245 | 2006-08-25 to 2026-09-23 | article field `Artigo0`; paragraph field `N_x00ba__x002f_Al_x00ed_nea` |
| CIMI | CIMI (Património) | `/patrimonio/cimi` | `Pages/Vinc_numero.aspx` | 42 | unfiltered | 96 | 96 | 2006-02-11 to 2025-12-29 | paragraph field `N_x00ba__x002f_Al_x00ed_nea` |
| CIMT | CIMT (Património) | `/patrimonio/cimt` | `Pages/Vinc_numero.aspx` | 40 | `Diploma = CIMT` | 221 | 221 | 2006-11-23 to 2026-07-13 | **numbered view is filtered** (188 entries); the unfiltered call adds 33 complementary-legislation rulings |
| CIUC | CIUC (Património) | `/patrimonio/ciuc` | `Pages/Vinc_numero.aspx` | 30 | `Diploma = CIUC` | 21 | 21 | 2011-02-07 to 2025-06-04 | numbered view is filtered (21 entries, same as unfiltered) |
| SELO | Imposto do Selo, heading "SELO" (Património) | `/patrimonio/selo` | `Pages/Vinc_numero.aspx` | 37 | unfiltered | 200 | 200 | 2006-11-23 to 2026-07-23 | `Diploma` is "Código Imposto Selo" (133) or "Tabela Geral do Imposto do Selo" (67); articles may be TGIS items |
| CIVA | CIVA (Despesa) | `/despesa/civa` | `Pages/visualizacao-por-numero-civa.aspx` | 51 | unfiltered | 2993 | 2993 | 2007-10-17 to 2026-09-10 (91 undated) | other page name; number `Vinc_x002e__x0020_n_x002e__x00ba_`, date `Data`, article `Anterior_x0020_Artigo` (shown as "Artigo"; `Artigo` is empty but for 1); Lista I/II rulings have articles such as `Verba 1.12` |
| RITI | RITI (Despesa) | `/despesa/riti` | `Pages/visualizacao-por-data-de-disponibilizacao-riti.aspx` | 32 | unfiltered | 43 | 43 | 2006-11-23 to 2026-08-28 (5 undated) | **no numbered view**; date view used; number `Vinc_x002e__x0020_n_x002e__x00ba_`; no `Diploma` in the fields of the inspected views; paragraph `N_x00ba__x002f_Al_x00ed_nea` |
| LGT | LGT (Justiça Tributária) | `/Justica_Tributaria/LGT` | `Paginas/default.aspx` | 1 | unfiltered | 6 | 6 | 2021-04-20 to 2025-04-10 (1 undated) | **no numbered view**; single page in `Paginas/`; fields reordered; one entry has only its PDF link |
| CESE | CESE (Contribuições Extraordinárias) | `/Contribuicoes_extraordinarias/CESE` | `Paginas/default.aspx` | 1 | unfiltered | 3 | 3 | 2020-05-15 to 2021-11-15 | **no numbered view**; page fields only `DocIcon,Assunto,Disponibilizada_x0020_em,NumeroVinculativa`; `Artigo` comes from the sort and is filled |
| CSB | CSB (Contribuições Extraordinárias) | `/Contribuicoes_extraordinarias/CSB` | `Paginas/default.aspx` | 1 | unfiltered | 2 | 2 | 2021-06-29 to 2021-10-12 | as CESE |

Proposed codes are the library headings, except EBF (heading "Estatuto dos Benefícios Fiscais"), DSRI (heading "Relações Internacionais", web path `DSRI`) and SELO (heading "SELO"). CIRS keeps its v0.1 code, endpoint and ids.

### Listing endpoint per library

The recommended call for the crawler is the chosen page's id with the **unfiltered** CAML and the superset fields below (every field that any checked view of that library displays or sorts by). The server applies the filter and field list it is sent, not the page's: for CIMT the unfiltered call with id 40 returns 221 entries, the page's own call 188, and the complementary-legislation view (id 36, `Diploma > CIMT`) 33, all contained in the 221.

| Code | Endpoint (`<web>/_vti_bin/portalat/docs.svc/listdocs`) | Superset `fields` | Page `sort` |
|---|---|---|---|
| CIRS | `/rendimento/cirs`, `id=42` | `DocIcon,NumeroVinculativa,Disponibilizada_x0020_em,Diploma,Artigo,Assunto,N_x002e__x00ba__x002f_Al_x00ed_nea,Assunto_Resumo` | `NumeroVinculativa:DESC,Disponibilizada_x0020_em:DESC,Artigo:ASC` |
| CIRC | `/rendimento/circ`, `id=43` | `DocIcon,NumeroVinculativa,Disponibilizada_x0020_em,Diploma,Artigo,Assunto,Anterior_x0020_Artigo,Assunto_Resumo,N_x002e__x00ba__x002f_Al_x00ed_nea` | as CIRS |
| DSRI | `/rendimento/DSRI`, `id=27` | `DocIcon,NumeroVinculativa,Disponibilizada_x0020_em,Diploma,Artigo,Assunto,N_x002e__x00ba__x002f_Al_x00ed_nea` | as CIRS |
| EBF | `/beneficios_fiscais`, `id=35` | `DocIcon,NumeroVinculativa,Disponibilizada_x0020_em,Diploma,Artigo0,Assunto,N_x00ba__x002f_Al_x00ed_nea,Assunto_Resumo` | `NumeroVinculativa:DESC` |
| CIMI | `/patrimonio/cimi`, `id=42` | `DocIcon,NumeroVinculativa,Disponibilizada_x0020_em,Diploma,Artigo,Assunto,N_x00ba__x002f_Al_x00ed_nea,Assunto_Resumo` | as CIRS |
| CIMT | `/patrimonio/cimt`, `id=40` | `DocIcon,NumeroVinculativa,Disponibilizada_x0020_em,Diploma,Artigo,Assunto,N_x002e__x00ba__x002f_Al_x00ed_nea,Assunto_Resumo` | `NumeroVinculativa:DESC` |
| CIUC | `/patrimonio/ciuc`, `id=30` | as CIMT | `NumeroVinculativa:DESC` |
| SELO | `/patrimonio/selo`, `id=37` | `DocIcon,NumeroVinculativa,Disponibilizada_x0020_em,Diploma,Artigo,Assunto,N_x002e__x00ba__x002f_Al_x00ed_nea` | `NumeroVinculativa:DESC` |
| CIVA | `/despesa/civa`, `id=51` | `DocIcon,Vinc_x002e__x0020_n_x002e__x00ba_,Data,Diploma,Anterior_x0020_Artigo,Assunto,N_x002e__x00ba__x002f_Al_x00ed_nea,Assunto_Resumo,Artigo` | `Vinc_x002e__x0020_n_x002e__x00ba_:DESC,Data:DESC` |
| RITI | `/despesa/riti`, `id=32` | `DocIcon,Disponibilizada_x0020_em,Vinc_x002e__x0020_n_x002e__x00ba_,Artigo,Assunto,N_x00ba__x002f_Al_x00ed_nea,Assunto_Resumo` | `Disponibilizada_x0020_em:DESC,Vinc_x002e__x0020_n_x002e__x00ba_:DESC,Artigo:ASC` |
| LGT | `/Justica_Tributaria/LGT`, `id=1` | `DocIcon,Artigo,N_x002e__x00ba__x002f_Al_x00ed_nea,Assunto,Disponibilizada_x0020_em,NumeroVinculativa,Diploma` | `Diploma:ASC,Artigo:ASC,N_x002e__x00ba__x002f_Al_x00ed_nea:ASC` |
| CESE | `/Contribuicoes_extraordinarias/CESE`, `id=1` | `DocIcon,Assunto,Disponibilizada_x0020_em,NumeroVinculativa,Artigo` | `Artigo:ASC,` (trailing comma as served) |
| CSB | `/Contribuicoes_extraordinarias/CSB`, `id=1` | as CESE | `Artigo:ASC,` |

### Field mapping per library

| Lupa Fiscal field | CIRS, CIRC, DSRI, CIMT, CIUC, SELO, LGT | EBF | CIMI | CIVA | RITI | CESE, CSB |
|---|---|---|---|---|---|---|
| process number | `NumeroVinculativa` | `NumeroVinculativa` | `NumeroVinculativa` | `Vinc_x002e__x0020_n_x002e__x00ba_` | `Vinc_x002e__x0020_n_x002e__x00ba_` | `NumeroVinculativa` |
| publication date | `Disponibilizada_x0020_em` | same | same | `Data` | `Disponibilizada_x0020_em` | same |
| diploma | `Diploma` | `Diploma` | `Diploma` | `Diploma` (CIVA, Lista I, Lista II, other laws) | none (use the tax code) | none (use the tax code) |
| article | `Artigo` | `Artigo0` | `Artigo` | `Anterior_x0020_Artigo` | `Artigo` | `Artigo` |
| paragraph | `N_x002e__x00ba__x002f_Al_x00ed_nea` | `N_x00ba__x002f_Al_x00ed_nea` | `N_x00ba__x002f_Al_x00ed_nea` | `N_x002e__x00ba__x002f_Al_x00ed_nea` | `N_x00ba__x002f_Al_x00ed_nea` | none |
| subject | `Assunto` | same | same | same | same | same |

Dates are ISO `yyyy-mm-dd` in a `<span>` everywhere. Entries without a date: CIRC 2, CIVA 91, RITI 5, LGT 1 (the crawler must accept a missing publication date; v0.1 assumed one is always present). Empty process numbers: CIRC 272, CIRS 72, DSRI 40, EBF 35, CIMI 9, CIMT 2, SELO 1, CIVA 98, RITI 9, LGT 1.

### Views compared with the chosen one

Besides the chosen views there are 42 other view links: 34 on the landing page and 8 library home pages (`Pages/default.aspx`) linked from the library pages. Run 2 inspected 22 of them. The 20 with a listing all read the same list as the chosen view (same web path, no `__libraries`, `listdocs`) with a restricting or the unfiltered filter, so their entries are a subset of the unfiltered call by construction; the CIRC and CIRS home pages carry no listing. Three were listed to confirm it: CIMT by article (id 35, 188 entries), CIMT complementary legislation (id 36, 33) and CIUC by article (id 20, 21); none lists a PDF missing from the superset. Not inspected (budget): the date views of DSRI, EBF, CIMI, CIMT, CIUC, SELO and CIVA, the subject views of EBF, CIMI, CIMT, CIUC, SELO, CIVA and RITI, and the home pages of DSRI, CIMI, CIMT, CIUC, SELO and RITI.

Notable filters: CIRS by subject uses `Diploma <> "zzz"`; the complementary-legislation views use `Diploma <> X` (CIRC, CIRS) or `Diploma > X` (CIMI, CIMT, CIVA), so a diploma sorting before the code (for example a decree-law named "Código ...") would appear in no browser view of CIMI, CIMT or CIVA. The unfiltered call avoids this.

### Not supported

| Page | Reason |
|---|---|
| Category pages `/rendimento`, `/patrimonio`, `/despesa`, `/Justica_Tributaria`, `/Contribuicoes_extraordinarias` (`Pages` or `Paginas/default.aspx`) | Navigation only: no `__itemid` or `__fieldnames`, no listing. Their links lead to the 13 libraries. |
| `Documents/Pedido_Informacao_Vinculativa_com_Instrucoes.pdf` | The request form, not a ruling. |
| `searchdocs` (paged) | Not used: every library page has `__enableSearch = 0`. |

Every one of the 13 libraries is supported.

## PDF URLs

All 5998 links are relative, resolve to `https://info.portaldasfinancas.gov.pt<web>/Documents/<file>.pdf` of their own library, and stay on the allowlisted host; the listings contained no off-host, http or non-PDF link. Within each library every PDF URL is distinct. One 5-byte sample per library (run 1) answered 200 `application/pdf` starting with `%PDF-`; Content-Length 57,569 to 94,504 bytes, except CSB (320,223). `PIV_<number>.pdf` names: CIRS 1071, CIVA 949, CIRC 394, EBF 150, CIMT 77, DSRI 57, SELO 34, RITI 15, CIMI 12, LGT 5, CIUC 2, CESE 1, CSB 0; the rest are free-form names with spaces and accents, so ids must stay sanitised file names (see gate0.md).

## Cross-library duplicates

219 file names (compared decoded and case-insensitively) appear in two libraries; none in three. 5779 distinct file names in total.

| Libraries | File names |
|---|---|
| CIRC + EBF | 109 |
| CIRS + EBF | 66 |
| CIMT + EBF | 21 |
| CIMI + EBF | 18 |
| CIRC + CIRS | 3 (`dl_74-99_01.pdf`, `piv_26167.pdf`, `piv_29555.pdf`) |
| CIMT + SELO | 1 (`imt_is_iv 16952.pdf`) |
| EBF + SELO | 1 (`ive_14004.pdf`) |

Most are EBF rulings also listed under the code whose tax they concern (CIRC lists 117 rulings with diploma EBF, CIRS 61, CIMT 22, CIMI 18). The URLs differ (each library has its own `Documents` folder), so a same name does not prove the same document: the crawler downloads both and records their SHA-256, and the index merges only byte-identical PDFs (decision of this run). Two different libraries can therefore hold a ruling with the same sanitised id, so ids must be unique per tax, with CIRS ids unchanged.

## Crawl estimate

Requests for a full multi-tax crawl: per tax one robots.txt and one listing (26), plus one per PDF: 5998 from scratch, or 4809 with the 1189 CIRS PDFs already cached. At the observed CIRS rate (1189 PDFs in 21 minutes, about 1.06 s per request at a 1 s minimum interval) that is about **85 minutes** for the new PDFs, or 106 minutes from scratch, in batches with `--max-downloads` (about 18 minutes per 1000). A listing response takes 0.04 to 0.5 s, CIVA (the largest, 1.5 MB) about 1 s.

Disk (estimates, not measured): at the CIRS average of 76 KB per PDF, about 455 MB of PDFs (365 MB new) and, at 9 KB of text per ruling, about 55 MB of text. Index: CIRS uses 38 MB and 5 passages per ruling; scaled to 5779 to 5998 rulings, about 29,000 to 30,000 passages, an index of about 190 MB and a vector matrix of about 45 MB (384 floats per passage), with about 20 minutes of CPU embedding for the new passages at the CIRS rate.

## Request logs

Paths are shown relative to `IV` = `/pt/informacao_fiscal/informacoes_vinculativas` on `https://info.portaldasfinancas.gov.pt`. Listing calls are summarised by their `id`, filter (unfiltered, or the page's own filter) and number of fields; the full URLs are in each run's `requests.json`. Bytes are the bytes read (5 for a PDF sample).

### Run 1 (2026-10-02, 60 requests)

| # | Time (UTC) | GET | Status | Bytes |
|---|---|---|---|---|
| 1 | 20:47:24.272 | `/robots.txt` | 404 | 683 |
| 2 | 20:47:25.886 | `IV/Pages/default.aspx` | 200 | 100899 |
| 3 | 20:47:27.600 | `IV/rendimento/circ/Pages/Vinc_numero.aspx` | 200 | 56528 |
| 4 | 20:47:29.273 | `IV/rendimento/circ/_vti_bin/.../listdocs (id=43, unfiltered, 6 fields)` | 200 | 331505 |
| 5 | 20:47:31.225 | `IV/rendimento/cirs/Pages/Vinc_numero.aspx` | 200 | 56537 |
| 6 | 20:47:33.054 | `IV/rendimento/cirs/_vti_bin/.../listdocs (id=42, unfiltered, 6 fields)` | 200 | 442820 |
| 7 | 20:47:34.968 | `IV/rendimento/DSRI/Pages/Vinc_numero.aspx` | 200 | 56589 |
| 8 | 20:47:36.687 | `IV/rendimento/DSRI/_vti_bin/.../listdocs (id=27, unfiltered, 6 fields)` | 200 | 50021 |
| 9 | 20:47:38.264 | `IV/beneficios_fiscais/Pages/Vinc_numero.aspx` | 200 | 56041 |
| 10 | 20:47:40.208 | `IV/beneficios_fiscais/_vti_bin/.../listdocs (id=35, unfiltered, 6 fields)` | 200 | 92740 |
| 11 | 20:47:41.811 | `IV/patrimonio/cimi/Pages/Vinc_numero.aspx` | 200 | 56536 |
| 12 | 20:47:43.502 | `IV/patrimonio/cimi/_vti_bin/.../listdocs (id=42, unfiltered, 6 fields)` | 200 | 39741 |
| 13 | 20:47:45.084 | `IV/patrimonio/cimt/Pages/Vinc_numero.aspx` | 200 | 56505 |
| 14 | 20:47:46.793 | `IV/patrimonio/cimt/_vti_bin/.../listdocs (id=40, page filter, 6 fields)` | 200 | 74402 |
| 15 | 20:47:48.384 | `IV/patrimonio/ciuc/Pages/Vinc_numero.aspx` | 200 | 56508 |
| 16 | 20:47:50.063 | `IV/patrimonio/ciuc/_vti_bin/.../listdocs (id=30, page filter, 6 fields)` | 200 | 8222 |
| 17 | 20:47:51.613 | `IV/patrimonio/selo/Pages/Vinc_numero.aspx` | 200 | 56489 |
| 18 | 20:47:53.307 | `IV/patrimonio/selo/_vti_bin/.../listdocs (id=37, unfiltered, 6 fields)` | 200 | 78989 |
| 19 | 20:47:54.904 | `IV/despesa/civa/Pages/visualizacao-por-numero-civa.aspx` | 200 | 56328 |
| 20 | 20:47:56.579 | `IV/despesa/civa/_vti_bin/.../listdocs (id=51, unfiltered, 6 fields)` | 200 | 1140606 |
| 21 | 20:47:58.870 | `IV/despesa/riti/Pages/visualizacao-por-data-de-disponibilizacao-riti.aspx` | 200 | 56541 |
| 22 | 20:48:00.582 | `IV/despesa/riti/_vti_bin/.../listdocs (id=32, unfiltered, 5 fields)` | 200 | 15566 |
| 23 | 20:48:02.135 | `IV/Justica_Tributaria/LGT/Paginas/default.aspx` | 200 | 57583 |
| 24 | 20:48:03.826 | `IV/Justica_Tributaria/LGT/_vti_bin/.../listdocs (id=1, unfiltered, 6 fields)` | 200 | 1755 |
| 25 | 20:48:05.372 | `IV/Contribuicoes_extraordinarias/CESE/Paginas/default.aspx` | 200 | 57186 |
| 26 | 20:48:07.077 | `IV/Contribuicoes_extraordinarias/CESE/_vti_bin/.../listdocs (id=1, unfiltered, 4 fields)` | 200 | 1305 |
| 27 | 20:48:08.678 | `IV/Contribuicoes_extraordinarias/CSB/Paginas/default.aspx` | 200 | 57165 |
| 28 | 20:48:10.365 | `IV/Contribuicoes_extraordinarias/CSB/_vti_bin/.../listdocs (id=1, unfiltered, 4 fields)` | 200 | 701 |
| 29 | 20:48:11.979 | `IV/rendimento/Pages/default.aspx` | 200 | 66233 |
| 30 | 20:48:13.670 | `IV/patrimonio/Pages/default.aspx` | 200 | 70864 |
| 31 | 20:48:15.347 | `IV/despesa/Pages/default.aspx` | 200 | 63032 |
| 32 | 20:48:17.005 | `IV/Justica_Tributaria/Paginas/default.aspx` | 200 | 55052 |
| 33 | 20:48:18.670 | `IV/Contribuicoes_extraordinarias/Paginas/default.aspx` | 200 | 56400 |
| 34 | 20:48:20.346 | `IV/rendimento/circ/Pages/legislacao-complementar.aspx` | 200 | 56079 |
| 35 | 20:48:22.054 | `IV/rendimento/cirs/Pages/legislacao-complementar.aspx` | 200 | 56751 |
| 36 | 20:48:23.872 | `IV/patrimonio/cimi/Pages/legislacao-complementar.aspx` | 200 | 56709 |
| 37 | 20:48:25.578 | `IV/patrimonio/cimt/Pages/legislacao-complementar.aspx` | 200 | 57507 |
| 38 | 20:48:27.277 | `IV/despesa/civa/Pages/legislacao-complementar-civa.aspx` | 200 | 57081 |
| 39 | 20:48:28.961 | `IV/patrimonio/selo/Pages/visualizacao-por-artigo-is.aspx` | 200 | 56546 |
| 40 | 20:48:30.654 | `IV/despesa/civa/Pages/artigos_CIVA.aspx` | 200 | 56593 |
| 41 | 20:48:32.334 | `IV/despesa/civa/Pages/Por_verba.aspx` | 200 | 56357 |
| 42 | 20:48:34.037 | `IV/rendimento/DSRI/Pages/assunto_artigo.aspx` | 200 | 57155 |
| 43 | 20:48:35.965 | `IV/rendimento/circ/Pages/circ-visualizacao-por-artigo.aspx` | 200 | 56541 |
| 44 | 20:48:37.676 | `IV/rendimento/cirs/Pages/cirs-visualizacao-por-artigo.aspx` | 200 | 56472 |
| 45 | 20:48:39.379 | `IV/beneficios_fiscais/Pages/visualizacao-por-artigo-ebf.aspx` | 200 | 56466 |
| 46 | 20:48:41.094 | `IV/patrimonio/cimi/Pages/visualizacao-por-artigo-cimi-4593.aspx` | 200 | 56475 |
| 47 | 20:48:43.029 | `IV/patrimonio/cimt/Pages/visualizacao-por-artigo-cimt.aspx` | 200 | 56477 |
| 48 | 20:48:44.709 | `IV/rendimento/circ/Documents/PIV_30410.pdf` | 200 | 5 |
| 49 | 20:48:46.257 | `IV/rendimento/cirs/Documents/PIV_31329.pdf` | 200 | 5 |
| 50 | 20:48:47.829 | `IV/rendimento/DSRI/Documents/PIV_30982.pdf` | 200 | 5 |
| 51 | 20:48:49.398 | `IV/beneficios_fiscais/Documents/PIV_30890.pdf` | 200 | 5 |
| 52 | 20:48:50.970 | `IV/patrimonio/cimi/Documents/PIV_26855.pdf` | 200 | 5 |
| 53 | 20:48:52.551 | `IV/patrimonio/cimt/Documents/PIV_30658.pdf` | 200 | 5 |
| 54 | 20:48:54.124 | `IV/patrimonio/ciuc/Documents/PIV_27836.pdf` | 200 | 5 |
| 55 | 20:48:55.691 | `IV/patrimonio/selo/Documents/PIV_30451.pdf` | 200 | 5 |
| 56 | 20:48:57.272 | `IV/despesa/civa/Documents/PIV_30858.pdf` | 200 | 5 |
| 57 | 20:48:58.855 | `IV/despesa/riti/Documents/PIV_30260.pdf` | 200 | 5 |
| 58 | 20:49:00.443 | `IV/Justica_Tributaria/LGT/Documents/PIV_28890.pdf` | 200 | 5 |
| 59 | 20:49:02.020 | `IV/Contribuicoes_extraordinarias/CESE/Documents/Ficha_Doutrinaria_PIV_16366.pdf` | 200 | 5 |
| 60 | 20:49:03.588 | `IV/Contribuicoes_extraordinarias/CSB/Documents/Ficha_Doutrinaria_PIV_21843.pdf` | 200 | 5 |

### Run 2 (2026-10-02, 60 requests, `--skip-samples`)

| # | Time (UTC) | GET | Status | Bytes |
|---|---|---|---|---|
| 1 | 20:53:04.701 | `/robots.txt` | 404 | 683 |
| 2 | 20:53:06.324 | `IV/Pages/default.aspx` | 200 | 100899 |
| 3 | 20:53:08.154 | `IV/rendimento/circ/Pages/Vinc_numero.aspx` | 200 | 56528 |
| 4 | 20:53:09.857 | `IV/rendimento/cirs/Pages/Vinc_numero.aspx` | 200 | 56537 |
| 5 | 20:53:11.535 | `IV/rendimento/DSRI/Pages/Vinc_numero.aspx` | 200 | 56589 |
| 6 | 20:53:13.225 | `IV/beneficios_fiscais/Pages/Vinc_numero.aspx` | 200 | 56041 |
| 7 | 20:53:14.905 | `IV/patrimonio/cimi/Pages/Vinc_numero.aspx` | 200 | 56536 |
| 8 | 20:53:16.598 | `IV/patrimonio/cimt/Pages/Vinc_numero.aspx` | 200 | 56505 |
| 9 | 20:53:18.291 | `IV/patrimonio/ciuc/Pages/Vinc_numero.aspx` | 200 | 56508 |
| 10 | 20:53:19.968 | `IV/patrimonio/selo/Pages/Vinc_numero.aspx` | 200 | 56489 |
| 11 | 20:53:21.660 | `IV/despesa/civa/Pages/visualizacao-por-numero-civa.aspx` | 200 | 56328 |
| 12 | 20:53:23.332 | `IV/despesa/riti/Pages/visualizacao-por-data-de-disponibilizacao-riti.aspx` | 200 | 56541 |
| 13 | 20:53:25.040 | `IV/Justica_Tributaria/LGT/Paginas/default.aspx` | 200 | 57583 |
| 14 | 20:53:26.734 | `IV/Contribuicoes_extraordinarias/CESE/Paginas/default.aspx` | 200 | 57186 |
| 15 | 20:53:28.434 | `IV/Contribuicoes_extraordinarias/CSB/Paginas/default.aspx` | 200 | 57165 |
| 16 | 20:53:30.136 | `IV/rendimento/Pages/default.aspx` | 200 | 66233 |
| 17 | 20:53:31.807 | `IV/patrimonio/Pages/default.aspx` | 200 | 70864 |
| 18 | 20:53:33.466 | `IV/despesa/Pages/default.aspx` | 200 | 63032 |
| 19 | 20:53:35.145 | `IV/Justica_Tributaria/Paginas/default.aspx` | 200 | 55052 |
| 20 | 20:53:36.832 | `IV/Contribuicoes_extraordinarias/Paginas/default.aspx` | 200 | 56400 |
| 21 | 20:53:38.495 | `IV/rendimento/circ/Pages/legislacao-complementar.aspx` | 200 | 56079 |
| 22 | 20:53:40.179 | `IV/rendimento/cirs/Pages/legislacao-complementar.aspx` | 200 | 56751 |
| 23 | 20:53:42.092 | `IV/patrimonio/cimi/Pages/legislacao-complementar.aspx` | 200 | 56709 |
| 24 | 20:53:43.784 | `IV/patrimonio/cimt/Pages/legislacao-complementar.aspx` | 200 | 57507 |
| 25 | 20:53:45.464 | `IV/despesa/civa/Pages/legislacao-complementar-civa.aspx` | 200 | 57081 |
| 26 | 20:53:47.162 | `IV/patrimonio/selo/Pages/visualizacao-por-artigo-is.aspx` | 200 | 56546 |
| 27 | 20:53:48.855 | `IV/despesa/civa/Pages/artigos_CIVA.aspx` | 200 | 56593 |
| 28 | 20:53:50.879 | `IV/despesa/civa/Pages/Por_verba.aspx` | 200 | 56357 |
| 29 | 20:53:52.566 | `IV/rendimento/DSRI/Pages/assunto_artigo.aspx` | 200 | 57155 |
| 30 | 20:53:54.284 | `IV/rendimento/circ/Pages/circ-visualizacao-por-artigo.aspx` | 200 | 56541 |
| 31 | 20:53:56.099 | `IV/rendimento/cirs/Pages/cirs-visualizacao-por-artigo.aspx` | 200 | 56472 |
| 32 | 20:53:57.811 | `IV/beneficios_fiscais/Pages/visualizacao-por-artigo-ebf.aspx` | 200 | 56466 |
| 33 | 20:53:59.511 | `IV/patrimonio/cimi/Pages/visualizacao-por-artigo-cimi-4593.aspx` | 200 | 56475 |
| 34 | 20:54:01.211 | `IV/patrimonio/cimt/Pages/visualizacao-por-artigo-cimt.aspx` | 200 | 56477 |
| 35 | 20:54:02.925 | `IV/patrimonio/ciuc/Pages/visualizacao-por-artigo-ciuc.aspx` | 200 | 56477 |
| 36 | 20:54:04.629 | `IV/despesa/riti/Pages/visualizacao-por-artigo-riti.aspx` | 200 | 56779 |
| 37 | 20:54:06.318 | `IV/rendimento/circ/Pages/circ-visualizacao-por-disponibilizacao.aspx` | 200 | 57145 |
| 38 | 20:54:08.013 | `IV/rendimento/circ/Pages/circ-visualizacao-por-assunto.aspx` | 200 | 56521 |
| 39 | 20:54:09.689 | `IV/rendimento/circ/Pages/default.aspx` | 200 | 59857 |
| 40 | 20:54:11.448 | `IV/rendimento/cirs/Pages/cirs-visualizacao-por-artigo-3637.aspx` | 200 | 56705 |
| 41 | 20:54:13.157 | `IV/rendimento/cirs/Pages/cirs-visualizacao-por-assunto.aspx` | 200 | 56122 |
| 42 | 20:54:14.982 | `IV/rendimento/cirs/Pages/default.aspx` | 200 | 59844 |
| 43 | 20:54:16.809 | `IV/rendimento/circ/_vti_bin/.../listdocs (id=43, unfiltered, 9 fields)` | 200 | 426107 |
| 44 | 20:54:18.680 | `IV/rendimento/cirs/_vti_bin/.../listdocs (id=42, unfiltered, 8 fields)` | 200 | 575648 |
| 45 | 20:54:20.694 | `IV/rendimento/DSRI/_vti_bin/.../listdocs (id=27, unfiltered, 7 fields)` | 200 | 50530 |
| 46 | 20:54:22.324 | `IV/beneficios_fiscais/_vti_bin/.../listdocs (id=35, unfiltered, 8 fields)` | 200 | 119611 |
| 47 | 20:54:24.046 | `IV/patrimonio/cimi/_vti_bin/.../listdocs (id=42, unfiltered, 8 fields)` | 200 | 53053 |
| 48 | 20:54:25.661 | `IV/patrimonio/cimt/_vti_bin/.../listdocs (id=40, unfiltered, 8 fields)` | 200 | 116822 |
| 49 | 20:54:27.327 | `IV/patrimonio/cimt/_vti_bin/.../listdocs (id=40, page filter, 6 fields)` | 200 | 74402 |
| 50 | 20:54:28.925 | `IV/patrimonio/ciuc/_vti_bin/.../listdocs (id=30, unfiltered, 8 fields)` | 200 | 10660 |
| 51 | 20:54:30.526 | `IV/patrimonio/ciuc/_vti_bin/.../listdocs (id=30, page filter, 6 fields)` | 200 | 8222 |
| 52 | 20:54:32.093 | `IV/patrimonio/selo/_vti_bin/.../listdocs (id=37, unfiltered, 7 fields)` | 200 | 80480 |
| 53 | 20:54:33.741 | `IV/despesa/civa/_vti_bin/.../listdocs (id=51, unfiltered, 9 fields)` | 200 | 1493522 |
| 54 | 20:54:36.292 | `IV/despesa/riti/_vti_bin/.../listdocs (id=32, unfiltered, 7 fields)` | 200 | 20191 |
| 55 | 20:54:37.906 | `IV/Justica_Tributaria/LGT/_vti_bin/.../listdocs (id=1, unfiltered, 7 fields)` | 200 | 1788 |
| 56 | 20:54:39.448 | `IV/Contribuicoes_extraordinarias/CESE/_vti_bin/.../listdocs (id=1, unfiltered, 5 fields)` | 200 | 1323 |
| 57 | 20:54:41.035 | `IV/Contribuicoes_extraordinarias/CSB/_vti_bin/.../listdocs (id=1, unfiltered, 5 fields)` | 200 | 711 |
| 58 | 20:54:42.627 | `IV/patrimonio/cimt/_vti_bin/.../listdocs (id=36, page filter, 4 fields)` | 200 | 10104 |
| 59 | 20:54:44.227 | `IV/patrimonio/cimt/_vti_bin/.../listdocs (id=35, page filter, 3 fields)` | 200 | 62098 |
| 60 | 20:54:45.860 | `IV/patrimonio/ciuc/_vti_bin/.../listdocs (id=20, page filter, 3 fields)` | 200 | 6858 |
