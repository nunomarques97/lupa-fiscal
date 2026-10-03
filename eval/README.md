# Retrieval eval set

Two frozen question sets in plain European Portuguese, each question with the rulings that answer it:

- `questions.json`: 50 questions about CIRS rulings. Results: [docs/eval/report.md](../docs/eval/report.md).
- `questions-taxes.json`: 24 questions about seven other taxes (IVA, IRC, IMT, Imposto do Selo, IMI, EBF, IUC), described [below](#other-taxes-questions-taxesjson). Results: [docs/eval/taxes/report.md](../docs/eval/taxes/report.md).

The `eval` command measures how often search puts one of the expected rulings in the first 10 results; `bench` measures how long hybrid search takes.

```
dotnet run --project src/LupaFiscal.Cli -- eval --questions eval/questions.json --out docs/eval/report.md --min-recall 0.85
dotnet run --project src/LupaFiscal.Cli -- eval --questions eval/questions-taxes.json --out docs/eval/taxes/report.md
dotnet run --project src/LupaFiscal.Cli -- bench --questions eval/questions.json --max-ms 1000
```

Both need the index (`index`) and the model. Each report has its data in `history.json` in the same folder. By default `eval` and `bench` only print their numbers and exit 1 below `--min-recall` or at `--max-ms`; they write nothing, so they can run as checks. Add `--record` to write the scores (with `--label` and `--note` to name an iteration) or the timings to the report and its history; recording unchanged scores leaves both files as they are. `eval --freeze` records only the question set's hash, before any measurement.

## Format

```json
{"questions": [{"id": "q01", "article": "10", "question": "...", "expected": ["piv_31329", "piv_30947"]}]}
{"questions": [{"id": "t01", "tax": "CIVA", "article": "9", "question": "...", "expected": ["civa-piv_25812", "civa-informacao_13014"]}]}
```

- `expected` lists ruling ids as stored in the index (sanitised PDF file names). Every id must exist in the index, or `eval` stops with exit 1 and names it.
- The rulings of one question are alternative answers: rulings on the same situation that reach the same conclusion. Finding any one of them answers the question.
- `article` is the article field of the main ruling as listed (usually CIRS; some rulings concern the EBF or a State Budget law), used for the coverage table below.
- `tax` (optional) is the tax of the main ruling, shown before the article in the report. It is a label only: questions are searched without filters, so it is not part of the frozen hash. The test of the multi-tax set checks that every expected id carries the question's tax prefix.

## How the CIRS questions were built

1. The 1189 indexed rulings were listed by article with their subject (the "Assunto" line), straight from the index database, without running any search.
2. Articles were picked in rough proportion to their share of the corpus, with at least one question for each smaller article that a taxpayer or accountant is likely to ask about (deductions, dependants, retention, special regimes).
3. For each question one ruling was chosen and its request and facts were read. The question restates that situation as a person would ask it: first person, everyday words ("vendi a casa onde morava", "passo recibos verdes"), no legal citations, and without copying the subject line. Technical terms stay only where people use them (IRS Jovem, PPR, mais-valias, residente não habitual).
4. The other rulings on the same situation were found by searching the subject list for that topic, and added to `expected` when their subject shows the same question. Search results were never used to choose answers, so the set does not favour any retrieval mode.
5. Every id was checked against the index. 19 questions have a single expected ruling; the median is 2 and the largest list has 19 (IRS Jovem and residente não habitual, where the tax authority issued many near-identical rulings).

The set was frozen on 2026-10-02, before any measurement. `eval` records its hash (`a770f8c678378a66...`, over ids, questions, articles and expected rulings, independent of JSON formatting) in `docs/eval/history.json` and refuses to measure, recorded or not, if the questions or answers change afterwards. A test checks the committed file against that hash.

## Coverage of the CIRS set

29 articles; their rulings make up 1031 of the 1189 indexed rulings.

| Article | Rulings in index | Questions | Ids | Topic |
|---|---|---|---|---|
| 10 | 358 | 8 | q01 to q08 | Capital gains: reinvestment of the family home, inheritance, crypto-assets, non-residents |
| 72 | 131 | 3 | q09 to q11 | Special rates: long-term rentals, non-habitual residents, short-held shares |
| 2 | 66 | 3 | q12 to q14 | Employment income: remote-work allowances, severance pay, health benefits |
| 12-B | 60 | 3 | q15 to q17 | IRS Jovem |
| 3 | 54 | 2 | q18, q19 | Self-employment income |
| 8 | 29 | 2 | q20, q21 | Property income |
| 21 (mostly EBF) | 29 | 2 | q22, q23 | PPR redemption |
| 41 | 25 | 2 | q24, q25 | Deductible landlord expenses |
| 51 | 24 | 2 | q26, q27 | Expenses deductible from capital gains |
| 12 | 22 | 2 | q29, q30 | Exclusions: foreign disability pension, accident compensation |
| 13 | 17 | 2 | q33, q34 | Household and dependants |
| 78-D | 14 | 2 | q37, q38 | Education expenses |
| 5 | 22 | 1 | q28 | Capital income: default interest |
| 81 | 20 | 1 | q31 | Double taxation relief for non-habitual residents |
| 87 | 19 | 1 | q32 | Disability deduction |
| 71 (CIRS or EBF) | 15 | 1 | q35 | Urban rehabilitation |
| 16 | 15 | 1 | q36 | Residence |
| 31 | 14 | 1 | q39 | Simplified regime |
| 12-A | 14 | 1 | q40 | Former residents (Programa Regressar) |
| 115 (CIRS or OE 2025) | 12 | 1 | q41 | Productivity bonuses |
| 45 | 11 | 1 | q42 | Acquisition value of gifts |
| 11 | 10 | 1 | q43 | Pensions: maintenance received |
| 101 | 10 | 1 | q44 | Withholding on self-employment income |
| 55 | 9 | 1 | q45 | Loss carry-forward |
| 78-C | 7 | 1 | q46 | Health expenses |
| 37 (EBF) | 7 | 1 | q47 | International organisations |
| 78-E | 7 | 1 | q50 | Rent deduction |
| 84 | 6 | 1 | q48 | Care homes |
| 83-A | 4 | 1 | q49 | Maintenance paid |

## Metrics

All metrics are at ruling level: the passages returned are reduced to distinct rulings, each at its first position, so several passages of one ruling count once. Each question is searched with no filters, one passage per ruling, 50 results deep.

- **recall@10**: share of questions with at least one expected ruling among the first 10 rulings. Because the expected rulings are alternatives, this is the usual question-answering retrieval measure: was a correct source retrieved.
- **MRR@10**: mean reciprocal rank of the first expected ruling within the first 10 (0 when none).
- **coverage@10**: share of a question's expected rulings found in the first 10, out of at most 10. Reported for transparency; not a target.
- Tied scores keep the order the searcher returns (its deterministic tie-break), which is the order a user sees.

Each run evaluates keyword-only (FTS5 BM25), vector-only (cosine) and hybrid (reciprocal rank fusion) search. The target is hybrid recall@10 >= 0.70.

## Iterations

An iteration changes retrieval parameters only (chunk size or overlap, candidate pool, fusion constant or weights, context added to chunks), never the questions or their answers. Each configuration, together with the number of rulings in the index, is one row of the report's history; rerunning it with `--record` updates its row, and a grown index adds a row so earlier scores stay visible. Name a row with `--label` and describe the change with `--note`.

On the CIRS-only index of v0.1 (1189 rulings) the baseline (the parameters chosen in the index design) reached hybrid recall@10 0.900, above the 0.70 target, so no tuning iteration was run: tuning on these 50 questions would mostly fit the parameters to them. On the full index of 5790 rulings over 13 taxes, with the same parameters (row "full index (13 taxes)"), hybrid recall@10 is 0.860 and MRR@10 0.632, above the v0.2 floor of 0.85: rulings of other taxes now compete for the top 10, and keyword search loses most (recall@10 0.800 to 0.740).

On the multi-tax set the baseline reached hybrid recall@10 0.833 (MRR@10 0.645), above its 0.70 target, so no tuning iteration was run there either. The two sets share one search configuration, so tuning for one would have to be checked against the other.

## Other taxes (`questions-taxes.json`)

24 questions over seven taxes other than CIRS, each with the rulings that answer it (90 in total), built on 2026-10-03 the same way as the CIRS set:

1. The rulings of each tax were listed by article with their subject, straight from the index database (5790 rulings over 13 taxes), without running any search.
2. Taxes were picked by size and by how often a person or a small business meets them; within each, the larger articles and topics a taxpayer or accountant is likely to ask about.
3. For one ruling per question its request, facts and conclusion were read, and the question restates that situation in everyday first-person words, without legal citations and without copying the subject line.
4. Other rulings on the same situation were added from the subject list of that tax (and article) when their subject shows the same question. Search results were never used to choose answers.
5. Every id was checked against the index. Expected ids are the index's display ids, so they carry the tax prefix (`civa-`, `circ-` and so on); a PDF also listed by an earlier tax is stored under that tax, and every chosen ruling is displayed under the question's tax. 2 questions have a single expected ruling, the median is 3 and the largest list has 8.

| Tax | Rulings in index | Questions | Ids | Topics |
|---|---|---|---|---|
| CIVA (IVA) | 2993 | 8 | t01 to t08 | Psychology, solar panels, short-term rentals, private tutoring, small-business exemption, home renovation at 6%, services to US clients, electric company car |
| CIRC (IRC) | 857 | 5 | t09 to t13 | Staff health insurance, electric car and autonomous taxation, losses from online fraud, tax transparency of law firms, rents of a non-profit |
| CIMT (IMT) | 204 | 3 | t14 to t16 | IMT Jovem with an undivided inheritance, IMT Jovem after moving for work, buying 75% of a property-owning company |
| SELO (Imposto do Selo) | 198 | 3 | t17 to t19 | Prize draws, cash gifts from parents, group cash pooling |
| CIMI (IMI) | 81 | 2 | t20, t21 | Who pays with a right of residence, short-term rental and property type |
| EBF | 73 | 2 | t22, t23 | IMI exemption for a family home, IMT exemption for urban rehabilitation |
| CIUC (IUC) | 21 | 1 | t24 | Disability exemption with the car in the spouse's name |

The set was frozen on 2026-10-03, before any measurement: `eval --freeze` recorded its hash (`29cdcc1b6214968d...`, computed as for the CIRS set) in `docs/eval/taxes/history.json`, and the first measurement came after. A test checks the committed file against that hash, the number of questions (at least 20) and taxes (at least 4, none CIRS), and the tax prefix of every expected id. Target: hybrid recall@10 >= 0.70.

## Limitations

- One author chose the questions and answers. The expected lists come from subject lines and may miss rulings that answer a question under a different subject, which can understate recall and coverage.
- The questions were written from rulings, so every question has an answer in the corpus. Real queries may not.
- 50 questions give a coarse measure: one question is 2 points of recall. In the multi-tax set one question is about 4 points.
- The multi-tax set covers 7 of the 12 other taxes; DSRI, RITI, LGT, CESE and CSB (174 rulings together) have no question. CIVA has the most questions because it holds half of the corpus.
