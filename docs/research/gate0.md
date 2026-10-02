# Gate 0: CIRS rulings source

Date: 2026-10-02. Outcome: **passed**. The CIRS listing and ruling PDFs are publicly reachable, robots.txt places no restriction, and the site terms permit reproduction for non-commercial use with the source cited. Nothing in the terms forbids automated download.

Evidence: `node scripts/gate0-probe.mjs` (3 requests, exit 0 on 2026-10-02):

```
[request 1/5] 2026-10-02T13:35:53.209Z GET https://info.portaldasfinancas.gov.pt/robots.txt -> 404
robots.txt: HTTP 404. RFC 9309 section 2.3.1.3: an unavailable robots.txt (4xx) means no crawl restrictions.
[request 2/5] 2026-10-02T13:35:54.812Z GET https://info.portaldasfinancas.gov.pt/pt/informacao_fiscal/informacoes_vinculativas/rendimento/cirs/_vti_bin/portalat/docs.svc/listdocs?fields=...&id=42 -> 200
Listing: 1189 entries, 1189 with a PDF link, 1189 distinct PDFs.
Listing: diploma "CIRS" 1106, other diplomas 83; dates 2006-08-24 to 2026-09-23.
[request 3/5] 2026-10-02T13:35:56.628Z GET https://info.portaldasfinancas.gov.pt/pt/informacao_fiscal/informacoes_vinculativas/rendimento/cirs/Documents/PIV_31329.pdf -> 200
PDF: ... -> application/pdf, Content-Length 63833, starts with %PDF-
Gate 0 probe passed with 3 request(s).
```

No ruling content was saved in the repository. The research samples (one listing response, one PDF) stay in a private scratch directory.

## robots.txt

`https://info.portaldasfinancas.gov.pt/robots.txt` returns **HTTP 404** (a SharePoint "not found" page). Under RFC 9309 section 2.3.1.3, an unavailable robots.txt (4xx status) means the crawler may access any resource on the host. The crawler must still fetch robots.txt at the start of each run and obey it if one appears; a 5xx or unreachable robots.txt means disallow all (RFC 9309 section 2.3.1.4).

## Site terms and legal notice

Reviewed: **Segurança e Privacidade**, `https://info.portaldasfinancas.gov.pt/pt/quem_somos/privacidade/Pages/privacidade.aspx`, linked from the footer of every listing page. It contains the privacy policy, the cookie policy and the section "Site da AT - Termos gerais e condições de uso". No other terms, licence or legal-notice page is linked from the CIRS pages.

Relevant passages (quoted verbatim):

> "O acesso e utilização da informação disponibilizada aos contribuintes neste site está sujeito às normas legais em vigor e aos termos e condições gerais aqui estabelecidos. Este site é de carácter informativo e transacional. Para efeitos informativos, a AT não exige qualquer registo do cidadão quando este interage com o seu site"

> "É permitida a reprodução da informação contida neste site desde que a fonte seja mencionada, se destine a fins não comerciais, tenha sido obtida de forma lícita e não conflitue com dados legalmente protegidos."

> "Este site não garante que um documento disponível em linha reproduza exatamente um texto adotado oficialmente. Assim, apenas a versão dos atos publicados no Diário da República é considerada autêntica."

> "A AT recolhe os seguintes dados dos visitantes do seu sítio da Internet: data e hora da consulta, endereço de IP (protocolo de Internet), tipo de navegador utilizado pelo visitante e as páginas acedidas."

Reading: the terms say nothing about automated access, neither allowing nor forbidding it. Reproduction is allowed under four conditions. How this project meets them:

| Condition | How Lupa Fiscal meets it |
|---|---|
| Source cited | Every result shows the ruling number and links to the official PDF on the official host. |
| Non-commercial | MIT open-source project, 0 EUR, no ads or paid tier. |
| Lawfully obtained | Public pages, no login, robots.txt honoured, rate limited, descriptive User-Agent. |
| No conflict with legally protected data | The published rulings are already anonymised (the sample shows the tax number and amounts as "xxx"). The corpus is not committed or redistributed in v0.1. |

The last quoted passage supports the UI notice: always read the official ruling.

Open point for the v1 public hosting gate (not v0.1): showing passages publicly is "reprodução". It stays within the terms only while the service is non-commercial and cites the source. The Sponsor should confirm this when deciding on public hosting.

## Legal basis for reuse

- **Lei Geral Tributária, artigo 68.º, n.º 17** (verified in the consolidated text at `https://www.pgdlisboa.pt/leis/lei_mostra_articulado.php?nid=253&tabela=leis`): "Todas as informações vinculativas prestadas, incluindo as urgentes, são publicadas no prazo de 30 dias por meios electrónicos, salvaguardando-se os elementos de natureza pessoal do contribuinte." Publication is a legal duty, and personal elements are removed before publication.
- Not verified in this gate (no source text was read): the Código do Direito de Autor (artigo 8.º, official texts and administrative decisions) and Lei n.º 26/2016 (access to and reuse of administrative documents). They are likely relevant but are not relied on here.

This is a research note, not legal advice.

## Listing endpoint

The CIRS list pages are SharePoint pages whose tables are filled by `/_layouts/15/portalat/js/listdocs.js` (jQuery DataTables). On the pages used (`__enableSearch = 0`) the script makes one call that returns the whole list; paging, sorting and filtering then happen in the browser.

| | |
|---|---|
| Host | `info.portaldasfinancas.gov.pt` (https) |
| Method | `GET`, no cookies, no authentication, `Accept: application/json` |
| URL | `https://info.portaldasfinancas.gov.pt/pt/informacao_fiscal/informacoes_vinculativas/rendimento/cirs/_vti_bin/portalat/docs.svc/listdocs` |
| `fields` | `DocIcon,NumeroVinculativa,Disponibilizada_x0020_em,Diploma,Artigo,N_x002e__x00ba__x002f_Al_x00ed_nea,Assunto` (columns come back in this order) |
| `sort` | `NumeroVinculativa:DESC` |
| `filter` | URL-encoded CAML `<IsNotNull><FieldRef Name="ID"></FieldRef></IsNotNull>` |
| `id` | `42` (page item id of `Pages/Vinc_numero.aspx`, "Visualização por Número") |
| Paging | None. One response holds every entry (about 450 KB, under 0.5 s). The `total` property is `0` and must not be used; count `data.length`. |
| Response | `application/json`: `{"data": [[...7 strings...], ...], "total": 0}` |
| CIRS total | **1189 entries** (2026-10-02), 1189 distinct PDFs |

The parameters come from the page variables `__fieldnames`, `__listsort`, `__filtervalue` and `__itemid`. The other CIRS views use the same endpoint with other ids: by article `id=33` (filter `Diploma = CIRS`), by subject `id=41`, articles 36/37 `id=35`. The numbered view is the superset used here; the paragraph field `N.º/Alínea` comes from the by-article view and is accepted by the same call. A paged variant (`docs.svc/searchdocs?...&pageSize=&pageNumber=`) exists in the script for pages with search enabled; CIRS does not use it.

Row example (after JSON decoding):

```
["<a href='/pt/informacao_fiscal/informacoes_vinculativas/rendimento/cirs/Documents/PIV_31329.pdf'><img src='/_layouts/15/images/icpdf.png' alt='PIV_31329.pdf' /></a>",
 "31329", "<span style='white-space: nowrap;'>2026-09-23</span>", "CIRS", "010", "n.º 07",
 "Reinvestimento do valor de realização de HPP na aquisição de dois imóveis- um destinado a HPP e outro destinado ao arrendamento- Aplicação temporal"]
```

Data observations (2026-10-02 listing):

- `NumeroVinculativa` is empty for 72 entries (older "ficha doutrinária" documents). Two numbers (12875, 29710) appear on two different PDFs each.
- `Diploma`: `CIRS` 1106; `Estatuto dos Benefícios Fiscais (EBF)` 61; the rest name State Budget laws, decree-laws or a tax treaty (22 in total).
- `Artigo`: 89 distinct values, mostly zero-padded to three digits with an optional suffix (`010`, `012-B`, `078-E`). A few are irregular (`10`, `12-B`, `0236`). Most frequent: `010` (355), `072` (131), `002` (66), `012-B` (59).
- `N.º/Alínea`: empty for 595 entries, otherwise free text such as `n.º 05, al. e)`, `n.º 01 al. a)`, `n.º1, al. d)`.
- Dates are ISO `yyyy-mm-dd` inside a `<span>`, from 2006-08-24 to 2026-09-23. No entries are dated 2014 to 2016, 2021 or 2023.
- `Assunto` is never empty and contains no HTML.

## PDF URLs

- Every `href` is relative and resolves to `https://info.portaldasfinancas.gov.pt/pt/informacao_fiscal/informacoes_vinculativas/rendimento/cirs/Documents/<file>.pdf`. The only host needed is **`info.portaldasfinancas.gov.pt`**.
- 1071 files are named `PIV_<number>.pdf`. The rest have free-form names with spaces, `º`, accents and descriptive suffixes (e.g. `PIV_12875_Premios_competicoes_desportivas_columbofilas.pdf`, `Ficha Doutrinária - Proc <n> <n>_PensoesDef_FArmadas.pdf`). The URL must be built with a URL parser (`new Uri(base, href)`), which percent-encodes them; cache file names must come from a sanitised id, never from the raw name.
- The file name does not always match `NumeroVinculativa` (e.g. number 29710 links to `PIV_30351.pdf`), so the number is not a safe key.
- Sample response: `200`, `Content-Type: application/pdf`, `Content-Length: 63833`, `ETag`, `Last-Modified`, `Cache-Control: private,max-age=0,no-cache`, body starts with `%PDF-1.5`. It has a text layer. The PDF repeats the metadata in a header ("INFORMAÇÃO VINCULATIVA / FICHA DOUTRINÁRIA", `Diploma`, `Artigo/Verba`, `Assunto`, `Processo: 31329, com despacho de 2026-09-22, ...`) and is structured in sections `I - PEDIDO`, `II. FACTOS DESCRITOS NO PEDIDO`, `III. INFORMAÇÃO`, and so on.

## Field mapping

| Lupa Fiscal field | Source | Rule |
|---|---|---|
| ruling id | PDF `href` file name | Unique across the listing (1189 of 1189). Sanitise to `[a-z0-9_-]` for ids and cache names. |
| tax | listing endpoint (CIRS library) | `CIRS` for every entry of this listing. Keep `Diploma` as its own field, because 83 CIRS entries concern another diploma (EBF, budget laws). |
| article | `Artigo` + `N.º/Alínea` | Strip leading zeros, keep the letter suffix (`010` to `10`, `012-B` to `12-B`). Store the paragraph text as is. The article belongs to `Diploma`. |
| date | `Disponibilizada_x0020_em` | Strip tags, parse `yyyy-mm-dd`. This is the publication date; the decision date ("com despacho de ...") is only in the PDF text. |
| process number | `NumeroVinculativa` | Use when not empty. Otherwise take it from the PDF `Processo:` line during extraction, or leave it empty. Not unique. |
| subject | `Assunto` | Plain text. |
| source URL | `href` | `https://info.portaldasfinancas.gov.pt` + `href`, percent-encoded. Store and link only https URLs on the allowlisted host. |

## Crawl estimate

Requests: 1 robots.txt + 1 listing + 1189 PDFs = 1191. At the chosen minimum of 1 request per second on one connection, plus about 0.1 to 0.5 s per transfer, a full CIRS crawl takes about **20 to 30 minutes**. Re-runs only fetch new PDFs, since the cache is kept on disk. Disk use is not measured; at the 64 KB sample size it would be roughly 75 MB (unverified). The listing is refreshed once per run.

## Crawler requirements from this gate

- User-Agent: `LupaFiscal/0.1 (+https://github.com/nunomarques97/lupa-fiscal; open-source research crawler)`. The repository is private for now; the URL becomes reachable when the Sponsor makes it public.
- Allowlisted host: `info.portaldasfinancas.gov.pt`, https only. Do not follow redirects to other hosts.
- Fetch robots.txt at the start of each run; a 404 means allow all.
- One request at a time, at least 1 s apart. Back off on 429 and 5xx.
- PDF identity is the file name. Neither the ruling number nor the `total` property is reliable.
