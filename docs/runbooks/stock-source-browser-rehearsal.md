# Stock-source staff browser rehearsal

Run this only against disposable local replica-set databases. It uses synthetic people, organization names, approved fixture grants and source settings; it does not establish real source acceptance or production access.

Follow [the shared stock browser setup](stock-count-browser-rehearsal.md) to install the pinned Node 24 / Playwright 1.62.1 runtime and build the frontend. Use the same UNITTOKO_STOCK_BROWSER, UNITTOKO_BROWSER_NODE, UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE, UNITTOKO_BROWSER_CHANNEL, PLAYWRIGHT_BROWSERS_PATH and guarded UNITTOKO_TEST_MONGO_URI environment variables. Run the two fixtures together with the filter:

    FullyQualifiedName~StockCountBrowserTests|FullyQualifiedName~StockSourceBrowserTests

Both must execute and pass; an opt-in skip is not evidence. The GitHub browser job checks that its TRX contains two executed passes and zero skipped tests.

The source fixture starts the built Next.js application and actual production-mode ASP.NET API behind a local HTTPS proxy. It signs in two distinct synthetic people through signed provider responses, state, nonce, PKCE, verified email and factor evidence. Only the provider front/back channel is synthetic; source requests, opaque cookies, CSRF, grants, person separation, transactions and persistence use application code.

Covered sequence:

1. Sign in as the source maker; verify protected host cookies and task discovery.
2. Reject missing/invalid CSRF, an unexpected actor and a foreign warehouse.
3. Review and submit an explicit web source; reject its maker's self-approval.
4. Submit an external source, deliberately lose two committed responses, verify the same key, reload, select the original warehouse and recover its receipt without resubmission.
5. Withdraw a third proposal through its explicit review.
6. Sign out, sign in as a separate reviewer, approve the web source and reject the external source.
7. Inspect mobile history, exclude private authority fields, then sign out.

Database assertions require three immutable proposals, three terminal decisions by the correct people, one inactive pool and physical-key registration, six corresponding audit events and receipts, and unchanged lot/balance/movement/reservation/accounting data. Approval must never activate stock.

Screenshots are written under tmp/production/stock-source-ui-results/browser/ and contain synthetic fixture information only. Inspect the desktop review and mobile history images before claiming visual verification. Bounded failure diagnostics include source-screen text and response statuses, never cookie/token bodies.

The fixture does not approve actual D01/D04 decisions, verify a live Auth0 tenant, accept OMI/S-Mart integration, deploy the .NET API, test full load/restore, or complete the overall production goal.
