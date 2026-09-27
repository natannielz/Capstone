# Q08 — legacy write-conflict acceptance

Date: 28 September 2026. Scope: the still-active legacy SQLite repository and its deterministic adapter regressions. This closes the bounded legacy fix in OPS05; it does not establish deployed libSQL network behavior or complete every production recovery gate.

The fourth recognized guard/command-uniqueness conflict now returns the existing typed recoverable HTTP 409 outcome. Before reporting exhaustion, the repository checks the exact persisted command actor and fingerprint and can return its original receipt. The write budget stays at four attempts. Unrelated transport errors remain unknown outcomes; merely mentioning a table name cannot turn an uncertain commit into a known conflict.

Evidence:

- The exhaustion test first returned a raw guard exception. The final command-key collision test also first returned its raw SQL exception.
- A separate classification regression demonstrated that an uncertain connection error containing `mutation_guards` was incorrectly retried. Explicit SQLite constraint recognition corrected it.
- All six repository cases pass: exhaustion without partial mutation, safe known-conflict retry, exact persisted receipt recovery, ambiguous commit followed by same-key recovery, seed time guard and the existing atomic workflow.
- Full frontend/legacy regression: 315/315, plus 6/6 future-date repository cases and 7/7 golden business scenarios. Zero skipped or failed. Node 24.19.0; run `node scripts/test-domain.mjs` from `frontend/`.
- TypeScript/production build pass. Independent source review found no remaining correctness issue in the bounded change.

The fault injection exercises the actual SQLite adapter and persisted receipt behavior, not a real remote network race. New Mongo business modules must separately retain their scoped idempotency and unknown-outcome tests as they are implemented. Existing authorization checks are unchanged.
