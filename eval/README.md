# Retrieval eval set

`questions.json` holds 50 questions in plain European Portuguese, each with the CIRS rulings that answer it. The `eval` command measures how often search puts one of those rulings in the first 10 results; `bench` measures how long hybrid search takes. Results: [docs/eval/report.md](../docs/eval/report.md).

```
dotnet run --project src/LupaFiscal.Cli -- eval --questions eval/questions.json --out docs/eval/report.md --min-recall 0.70
dotnet run --project src/LupaFiscal.Cli -- bench --questions eval/questions.json --max-ms 1000
```

Both need the CIRS index (`index`) and the model. `eval` writes `docs/eval/report.md` and its data, `docs/eval/history.json`; rerunning it with unchanged scores leaves both files as they are. Timings change on every run, so `bench` only prints them unless `--record` is added, which writes them to the same files.

## Format

```json
{"questions": [{"id": "q01", "article": "10", "question": "...", "expected": ["piv_31329", "piv_30947"]}]}
```

- `expected` lists ruling ids as stored in the index (sanitised PDF file names). Every id must exist in the index, or `eval` stops with exit 1 and names it.
- The rulings of one question are alternative answers: rulings on the same situation that reach the same conclusion. Finding any one of them answers the question.
- `article` is the article field of the main ruling as listed (usually CIRS; some rulings concern the EBF or a State Budget law), used for the coverage table below.

## How the questions were built

1. The 1189 indexed rulings were listed by article with their subject (the "Assunto" line), straight from the index database, without running any search.
2. Articles were picked in rough proportion to their share of the corpus, with at least one question for each smaller article that a taxpayer or accountant is likely to ask about (deductions, dependants, retention, special regimes).
3. For each question one ruling was chosen and its request and facts were read. The question restates that situation as a person would ask it: first person, everyday words ("vendi a casa onde morava", "passo recibos verdes"), no legal citations, and without copying the subject line. Technical terms stay only where people use them (IRS Jovem, PPR, mais-valias, residente não habitual).
4. The other rulings on the same situation were found by searching the subject list for that topic, and added to `expected` when their subject shows the same question. Search results were never used to choose answers, so the set does not favour any retrieval mode.
5. Every id was checked against the index. 19 questions have a single expected ruling; the median is 2 and the largest list has 19 (IRS Jovem and residente não habitual, where the tax authority issued many near-identical rulings).

The set was frozen on 2026-10-02, before any measurement. `eval` records its hash (`a770f8c678378a66...`, over ids, questions, articles and expected rulings, independent of JSON formatting) in `docs/eval/history.json` and refuses to record an iteration if the questions or answers change afterwards. A test checks the committed file against that hash.

## Coverage

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

An iteration changes retrieval parameters only (chunk size or overlap, candidate pool, fusion constant or weights, context added to chunks), never the questions or their answers. Each configuration is one row of the report's history; rerunning the same configuration updates its row. Name an iteration with `--label` and describe the change with `--note`.

The baseline (the parameters chosen in the index design) reached hybrid recall@10 0.900, above the target, so no tuning iteration was run: tuning on these 50 questions would mostly fit the parameters to them.

## Limitations

- One author chose the questions and answers. The expected lists come from subject lines and may miss rulings that answer a question under a different subject, which can understate recall and coverage.
- The questions were written from rulings, so every question has an answer in the corpus. Real queries may not.
- 50 questions give a coarse measure: one question is 2 points of recall.
