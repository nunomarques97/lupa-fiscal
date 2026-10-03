# Design: Lupa Fiscal v0.1

Chosen direction: **A, citation register**. Mocks and renders live in `docs/ui/`; recapture them with `npm run ui:directions` (Playwright with the Chromium already installed, no browser download). The mocks use real output of the local CIRS index (`docs/ui/mock-data.js`, from `GET /api/search` and `GET /api/facets`).

## Directions compared

| Direction | Mock | Renders |
|---|---|---|
| A, citation register (chosen) | `docs/ui/direction-a.html` | `direction-a-citation-register-1440.png`, `-390.png`, plus states `-inicial-`, `-carregar-`, `-vazio-`, `-erro-` at both widths and `-filtros-390.png` |
| B, accountant console | `docs/ui/direction-b.html` | `direction-b-accountant-console-1440.png`, `-390.png` |
| C, question and evidence | `docs/ui/direction-c.html` | `direction-c-question-evidence-1440.png`, `-390.png` |

**A is confirmed.** The renders show no defect that would overturn it. At 1440 px the first screen shows the query, all filters, the notice and two full results with their quoted passage and citation; at 390 px it shows the notice, query, filter summary and one full result.

**B, rejected.** The 1440 render shows eight subject lines but only one passage, and at 390 px only the first card has a passage. The passages are what answer the question, and the product promise is that every statement comes from a cited ruling; B makes the user open rows to see any evidence. The subject column also wraps for long subjects and leaves a wide empty strip beside the expanded passage. Its useful idea (process number, article and date in fixed positions for scanning) is kept in A's citation column.

**C, rejected.** The question echoed as a large heading pushes the first passage about 300 px down at both widths, and the citation becomes a quiet grey line after the passage, so the source reads as an afterthought. Without the subject line the results are hard to tell apart when scanning ten of them, and the filters collapse to one sentence that hides what can be filtered.

## Organising rule

Every passage is shown with its source citation. The citation (process number, article, date, section) is a fixed column beside the passage on desktop and a fixed line above it on mobile; it is never optional, never collapsed and never shown without a link to the official PDF. Dominant: the passage. Secondary: the subject and the citation. Quiet: the summary line, timing and footer.

## Layout

- Desktop (more than 720 px): content max width 1240 px, side padding 32 px. Grid: filter rail 232 px, gap 48 px, main column. The rail is sticky (`top: 16px`) and sized to its content.
- Result item: grid with a 168 px citation column, 24 px gap, and the body (subject, passage, actions). Passage line length at most 68ch.
- Header: wordmark and one-line tagline. Directly below, the notice band at full width. Footer: independence statement.

### 390 px reorganisation

- The rail is removed. A **Filtros** button (with the number of active filters, e.g. "Filtros (2)") and a one-line summary of the active filters ("IRS · artigo 99.º-F · 2006") sit under the search box. The button opens a bottom sheet (modal dialog) with the same three selects and **Limpar** / **Aplicar filtros**.
- The search button goes under the input at full width (48 px tall).
- The citation column becomes two lines above the subject: "1 Processo 15223" then "IRS · Art. 13.º · 15/04/2020 · Secção: Texto". It stays identifiable; it is never hidden.
- The tagline is hidden; the notice keeps its first two sentences and the independence statement moves to the footer only.
- Passage excerpt target 240 characters (360 on desktop), serif 17 px.

## Type

System fonts only: no web fonts, no requests to third-party hosts, nothing to licence.

- Sans (UI, subject, citation): `system-ui, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif`.
- Serif (passages, wordmark): `Charter, "Bitstream Charter", "Sitka Text", Cambria, Georgia, serif`.
- Numbers use `font-variant-numeric: tabular-nums`.

| Role | Size / line height | Weight |
|---|---|---|
| Wordmark | 20 / 1.2 serif | 600 |
| State heading (empty, error, initial) | 20 / 1.3 | 600 |
| Search input | 18 (17 at 390) | 400 |
| Passage | 18 / 1.6 serif (17 at 390) | 400 |
| Subject (h3) | 17 / 1.35 | 600 |
| Body, buttons, labels | 16 / 1.5 | 400, labels 600 |
| Process number | 15 (14 at 390) | 600 |
| Citation, actions, notice, summary, footer | 14 to 15 / 1.45 | 400 |
| Rail heading and rank | 13, uppercase heading with .06em tracking | 600 |

## Colour

Paper and ink with an oxblood accent and a marker yellow highlight. No official logos, coats of arms or colours of the tax authority or the state; the wordmark is plain type with a small ring.

| Token | Value | Use | Contrast (WCAG 2.x) |
|---|---|---|---|
| `--paper` | `#fbfaf7` | page background | |
| `--paper-2` | `#f2eee5` | notice band, skeleton bars | |
| `--ink` | `#1c1a17` | text, primary button | 16.6:1 on paper, 15.0:1 on paper-2 |
| `--ink-2` | `#48443d` | secondary text, citation | 9.3:1 on paper, 8.4:1 on paper-2 |
| `--ink-3` | `#686358` | quiet text (summary, footer, rank) | 5.7:1 on paper, 5.2:1 on paper-2 |
| `--accent` | `#8a2b1a` | links ("Abrir PDF oficial", "Limpar filtros"), wordmark ring | 8.2:1 on paper, 6.6:1 on mark |
| `--mark` | `#f7e08c` | highlight background | ink on mark 13.2:1 |
| `--danger` | `#9b1c1c` | error heading | 7.8:1 on paper |
| `--focus` | `#1f4fd1` | focus ring | 6.5:1 on paper (3:1 non-text needed) |
| border | `#8f897d` | input, select and quiet button borders | 3.3:1 on paper (3:1 non-text needed) |
| `--rule` | `#d8d2c6` | dividers only (decorative) | |
| white on ink | | primary button text | 17.4:1 |

All text pairs pass AA (4.5:1) and AAA for body text except `--ink-3`, which passes AA.

## Spacing

4 px base scale: 4, 8, 12, 16, 24, 32, 48. Result items have 24 px vertical padding (16 at 390) and a 1 px rule between them; the summary line sits on a 2 px ink rule that marks where results start.

## Components

- **Search box**: visible label "A sua pergunta" above a search input (52 px tall) and a dark **Pesquisar** button. Enter submits. `role="search"` on the form.
- **Filter controls**: native `<select>` elements with visible labels: **Imposto** ("Todos" then every indexed tax by its name, sorted: "IRS" for CIRS, "IVA" for CIVA, "Imposto do Selo" for SELO; an unknown code is shown as it is), **Artigo** (disabled, with the hint "Escolha primeiro um imposto." under it, until a tax is chosen; then "Todos" and only that tax's articles, as "13.º", "78.º-D" or as published, such as "Verba 1.12"; changing or clearing the tax clears it), **Ano de publicação** ("Todos" then years, newest first). Each option shows the facet count in parentheses. **Limpar filtros** is a text button. Values come from `/api/facets`.
- **Result item** (`<li>` in an `<ol>`): citation column with rank, "Processo N", the tax ("IRS"), the article ("Art. 13.º", or as published), date as dd/mm/aaaa in `<time datetime>`, and "Secção: …" (header Identificação, request Pedido, facts Factos, legal-framework Enquadramento, conclusion Conclusão, content Texto); subject as `<h3>`; passage in a `<blockquote cite>`; actions **Abrir PDF oficial** (`target="_blank" rel="noopener noreferrer"`, with hidden text "(abre num novo separador)") and **Ler passagem completa** (toggle with `aria-expanded`, shows the whole passage in place).
- **Passage excerpt**: PDF line breaks become spaces one for one, so highlight offsets stay valid. The excerpt starts at the beginning of the passage (which usually states the question) unless a later window holds at least two more highlights, is snapped to word boundaries, and shows "…" where text is cut. Reference implementation: `docs/ui/mock-common.js`.
- **Highlight**: `<mark>` built from the API's `{start, length}` offsets as text segments (never innerHTML), merged when they overlap. Marker yellow background, text colour unchanged, 2 px radius.
- **Notice**: a full-width band under the header, always the first content after the wordmark: "**Não é aconselhamento fiscal.** Leia sempre a informação vinculativa oficial." plus, on desktop, "Projeto independente, sem ligação à Autoridade Tributária e Aduaneira." It is visible without scrolling at both widths and never dismissible.
- **Summary line**: "8 informações vinculativas, por proximidade" and the time in ms (desktop only).
- **States** (they replace the results region in one transition; the query and filters stay as submitted):
  - Initial: heading "Escreva a sua pergunta como a faria a um contabilista", one explanatory sentence and three example questions as links.
  - Loading: summary "A pesquisar…", four skeleton rows in the result grid, `aria-busy="true"` on the region. Previous results are not shown as results of the new query.
  - Empty: "Nenhuma informação vinculativa encontrada", a suggestion to remove a filter or reword, and **Limpar filtros**.
  - Error: heading in `--danger` "Não foi possível pesquisar", "O serviço de pesquisa não respondeu. A sua pergunta e os filtros mantêm-se." and **Tentar de novo**.
  - Validation (blank query): the same inline pattern under the input, "Escreva uma pergunta para pesquisar.", with `aria-describedby` on the input.
- **Live region**: one visually hidden `aria-live="polite"` element announces once per completed search: "8 informações vinculativas encontradas.", "Nenhuma informação vinculativa encontrada." or "Não foi possível pesquisar." The error block itself is not `role="alert"`, so it is announced once.

## Focus and keyboard

- `:focus-visible` outline 3 px `--focus`, offset 2 px, on every link, button, input and select. Never removed.
- Focus stays on the control the user used (input after Enter, the select after a filter change, **Tentar de novo** after retry).
- The mobile filter sheet is a modal dialog: focus moves to its first select on open, Escape closes it, and focus returns to the **Filtros** button.
- Touch targets at least 44 px tall at 390 px (search input 52, buttons 44 to 48, selects 44 in the sheet; 40 on desktop).

## Motion

Motion is never needed to understand a state. With `prefers-reduced-motion: no-preference`, skeleton bars pulse (opacity, 1.6 s) and the sheet may slide up in about 150 ms. With `reduce`, the skeleton is static and the sheet appears in place; the static equivalent of loading is the "A pesquisar…" summary line.

## Copy rules

European Portuguese, no em-dashes, nothing about how the product was built. Never "oficial" except for the ruling and its PDF. Never suggest a link with the tax authority beyond the independence statement.

## Known weaknesses

- At 390 px a long question is clipped inside the single-line input; the full text stays editable and the results show their own context.
- The article select of a tax has up to about 160 options (IVA). Acceptable with native selects; a searchable list would be a later change.
- Subjects come from the rulings and can contain en-dashes; they are quoted as published.
