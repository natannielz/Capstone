# Stock-count real browser/API verification — 28 September 2026

Status: local integration verified. The new CI job, independent implementation review, real Auth0/public hosting and pilot acceptance remain separate gates.

This supplements the [screen verification](stock-count-staff-screen-verification-2026-09-28.md). The earlier eleven rendered-browser groups used intercepted synthetic API responses. This test uses actual API responses and Mongo transactions; it does not replace the earlier expiry, stale-read, storage-failure and responsive/keyboard coverage.

## Test boundary

The production Next.js build and the .NET application in Production environment run behind one local HTTPS edge. Kestrel, authorization, anti-forgery, session protection, catalog choices, immutable observations, audit records and receipt persistence are the real implementations. A generated database on the guarded local replica set holds synthetic accounts and catalog data.

The browser uses the real sign-in form and callback. The provider front/back channels are synthetic, with a signed proof fixture checked by the maintained OIDC handler, nonce/correlation/state validation and PKCE. Session keys are certificate-protected on disk. The temporary TLS certificate is self-signed, accepted only by this test browser context; public TLS trust and the actual Auth0 tenant remain unproven.

The test proxy, provider page and runner are test-only source. They are not application routes or a deployment configuration. The CONNECT tunnel permits only the two named fixture hosts and maps them to loopback without changing system DNS. No real provider, email, business account or production database is contacted.

## Results

- The final seven browser groups pass on Windows with Edge and with pinned Chromium (Playwright 1.62.1). The final Chromium run additionally asserts the encrypted-key directory is nonempty.
- Seven groups cover sign-in/cookies, real CSRF/actor/warehouse rejection, a same-site different-origin request, trusted forwarding, zero-count persistence/history, interrupted committed-response recovery, and logout.
- The different-origin browser request actually carried its session cookie and was rejected with untrusted_origin. This checks origin enforcement beyond merely observing cookie settings.
- Two deliberately truncated responses follow the same real committed command. The browser retains one original reference through retry and reload; the real receipt clears it without another write.
- After browser completion, C# verifies exactly two observations, two count audits and two receipts; package/base-unit quantities are 0/0 and 2/24. Both observation references exceed JavaScript's safe-integer range and retain the correct actor. Physical-stock/accounting collections remain unchanged, and logout leaves no active session.
- The affected provider/full-host group passed 13 tests, zero failures/skips, 56 seconds. This included the six-group browser version; the additional different-origin group and nonempty-key assertion were then verified in focused final browser runs.
- Final focused Chromium run: one executed/passed test, zero failures/skips, 11 seconds, covering all seven browser groups plus the database/key assertions. Final seven-group Edge run: one passed, 14 seconds, before the additional nonempty-key assertion.
- Frontend typecheck, import boundaries and harness lint passed. The updated three-job YAML parsed successfully, and its browser job paths/opt-in/counter checks were inspected. Clean installation from the new isolated browser lockfile succeeded.

The new Browser, HTTPS and real Mongo CI job builds the frontend, starts its own replica set and runs this opt-in test in pinned Chromium. It validates the TRX counters so skipped or missing execution fails the job. The ordinary backend run explicitly skips this browser-only case; the separate browser job must actually execute it.

## Published screen verification

[GitHub workflow 36437605314](https://github.com/natannielz/Capstone/actions/runs/36437605314) completed successfully for screen commit d8566dbc69071a44bf9c1e0b079e94e7547148c2: 1,663 backend passes, zero failures, 71 explicit Windows-only method skips on Linux, 1,734 total; frontend 491+6+7, Auth0 Action fixtures, both contracts and production builds passed. The backend test step took 33 minutes 17 seconds. Its full log is tmp/production/github-publication-results/run-36437605314.log, SHA-256 d89c3d188e30f4fb557ffb1ecc3181d956ade75ed079a219b10a3e3f72ee097b. This verifies the published screen commit; the new browser integration and CI job require their own subsequent workflow.

## Failure investigation

The first harness run reached real sign-in, protection checks and zero-count history but did not reach the completion marker. Failure capture was improved before diagnosis. A diagnostic run showed Chromium transparently replayed a request after a reset before response headers; the server returned its existing same-key result and the screen correctly showed success.

The fault fixture was changed to consume the real successful upstream response, send headers plus an incomplete JSON body, and then cut the connection. This reliably leaves an unknown delivery outcome. It does not modify application retry, idempotency or authorization behavior. The following run passed, including unchanged-key deliberate retry and receipt recovery. The marker and database assertions prevent early child exit from becoming a false pass.

## Reproduction and remaining gates

Follow [the rehearsal runbook](../runbooks/stock-count-browser-rehearsal.md). It records runtime/dependency commands, temporary-resource cleanup, synthetic provider/TLS boundaries and the CI invocation.

Independent review of the later choice/API/client/screen and this harness remains pending; the three review agents still report account usage limits. Root inspection and passing tests do not replace that review. Deployed network isolation, trusted certificates, the actual Auth0 service and security-event coverage, operational load/restore, the remaining production/report modules and an accepted branch pilot remain open. Vercel owning-account access is still missing; no new deployment or .NET/Mongo/Auth0 activation is claimed.

## Evidence

Paths below are relative to tmp/production/stock-count-results unless prefixed with source. These files contain synthetic fixture evidence only.

| Artifact | SHA-256 |
| --- | --- |
| stock-count-real-browser-first.trx | 8b5f9e1c0a05e8b2caad443a6be65832a5bd2b3b481d6ccb2a8ffa86f1c55fc4 |
| stock-count-real-browser-network-diagnostic.trx | 3cf38ddb3add4fc2fa13f637d1f51f770cdd97b07e726894810b73264244bdbe |
| stock-count-real-browser-body-loss.trx | 42b5214b3474e9df750553b145f64240da4ae37b4b7240d01c2067cf7acba215 |
| stock-count-browser-and-provider-final.trx | e5dcb75250beceb51602558d65772c9f3bbb6961790d5595b66772d3e58645bf |
| stock-count-browser-origin-chromium.trx | 907f4c113e0d82d45bf1750a816cb413168c77597c75b194f77348fb9d99bd28 |
| stock-count-browser-origin-edge.trx | 1a35ce32fc7ff75a543fd688381bde4ba545f19fb9539e50df90438e2b960e4d |
| stock-count-browser-final-key-assertion.trx | 6aba12738cfc402c59963a1d9bb0ff2ed2943c31272a693e30ac61a51c7b0d41 |
| source: backend/tests/UnitToko.Tests/Inventory/StockCountBrowserTests.cs | e95fed0f9cf402ce7fb7257372e3b1ebf05610eac9f251286447e3c8fbaa2e43 |
| source: frontend/scripts/verify-stock-count-browser.mjs | 966b8c9422516e11b6fe5d88f96703e4066127acef1509e18e2b7248af1fbd09 |
| source: frontend/tests/browser/package-lock.json | c293897c6c3d7a94fc08999b580bb7cda14e62c11998d66fc65805a7363f131f |
