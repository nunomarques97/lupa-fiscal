---
status: bootstrap
sponsor_action: none
kill_review: after v0.1 eval (recall@10 below 70 % after two iterations stops the project as a documented study)
success_metric: recall@10 >= 70 % on the 50-question eval set, search under 1 s locally
---

# State

## Current
Project bootstrapped. v0.1 implementation not started.

## Next
1. Gate 0: confirm the public rulings index and PDFs are reachable and that robots.txt and the site terms allow automated download for this purpose.
2. v0.1 (local only): crawler (CIRS first), extraction, chunking, local embeddings, hybrid search, API, Angular UI, eval.

## Roadmap
- v0.1: local semantic search over CIRS rulings, no generated answers.
- v0.2: every tax, plus answer synthesis with strict citations.
- v1: public hosting (Sponsor gate).

## Decisions
- 2026-10-02: no Linear; priorities and roadmap live here.
- 2026-10-02: repository private; making it public is a Sponsor decision.
