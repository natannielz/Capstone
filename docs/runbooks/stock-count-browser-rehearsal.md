# Real stock-count browser rehearsal

This is a repeatable local/CI integration test, not a deployment proxy or production bootstrap. It runs the actual production Next.js build, the .NET application in Production environment, protected persistent session keys, real authorization/anti-forgery middleware and a disposable Mongo replica-set database. Only the provider responses, accounts, catalog and local certificate are synthetic.

The browser completes the real application's sign-in form, state/nonce/correlation/PKCE callback, scoped count/recovery/history and logout flows. No API response is intercepted or replaced with a mock. One deliberate transport fault truncates successful count responses after the real server has committed them.

## Topology and safety boundaries

- MongoFixture permits only 127.0.0.1:27028, replica set rs0, with generated test databases. Existing production databases cannot be selected through this fixture. The fixture drops its own generated databases after the run.
- WebApplicationFactory uses a real Kestrel listener bound to 127.0.0.1 on a dynamic port. The API is explicitly in Production environment; its normal identity/authentication/authorization services are retained.
- The test-only Node harness starts the production Next.js handler, a local HTTPS edge and a CONNECT tunnel on dynamically assigned loopback ports. It is never imported into the application or used by Vercel.
- Browser requests to portal.unit-toko.co.id and login.unit-toko.co.id travel through the private test tunnel to loopback. The tunnel rejects all other destinations; it does not change hosts files or system DNS.
- The edge routes /api/v1 and /auth to the API and pages/assets to Next.js. It removes inbound forwarded/original headers and supplies one exact host, HTTPS scheme and observed client-address hop. Only the loopback proxy is trusted by the API.
- The provider front channel redirects locally. The back channel uses the existing signed provider fixture, while the maintained OIDC handler and actual local sign-in completion still validate and persist the result. No real Auth0 endpoint, account, email or enrollment service is called.
- Synthetic TLS material goes over the child-process input pipe, not process arguments or a saved configuration. The fixture uses a self-signed certificate and browser-local ignoreHTTPSErrors. It proves encrypted transport and browser cookie behavior, not a trusted public certificate or real provider acceptance.
- Session keys use the existing certificate-protected persistent-key implementation. The test checks encrypted key XML. Temporary keys, browser processes, listeners and databases are disposed after the run; the parent enforces a four-minute browser-process deadline.

Framework references: [.NET 10 real-server integration support](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.testing.webapplicationfactory-1.usekestrel?view=aspnetcore-10.0) and [Playwright CI setup](https://playwright.dev/docs/ci). The local Next.js custom-server guide was checked against the installed version; this custom server exists only inside the test harness.

## What must pass

1. Anonymous staff navigation reaches the real sign-in form and returns through the real callback to /inventory/counts. The account reference remains an exact Int64 string.
2. The browser accepts host-only, Secure, HttpOnly session and anti-forgery cookies with their configured SameSite policies. JavaScript cannot read those cookies; callback correlation/nonce cookies have been consumed.
3. Actual browser requests with absent/bad anti-forgery proof, a different expected actor or an unauthorized warehouse are rejected. A same-site request from a different origin is rejected even when the browser sends its session cookie. Browser-supplied forwarding headers cannot override the test proxy's canonical origin.
4. A deliberate zero count is saved, read back through actual history and shown with the original package conversion. Private physical-item keys do not enter the displayed projection.
5. Two truncated deliveries of one committed two-package count retain the same original key. Reload preserves only actor/operation/key/time; an actual receipt lookup resolves it without another write or a close/abandon action.
6. Actual logout revokes the stored session and removes the browser session cookie.

After the browser completes, C# independently verifies exactly two Mongo observations (0 and 2 packages; 0 and 24 base units), large numeric references, the correct actor, exactly two count audits and receipts, unchanged physical-stock/accounting collections, and no remaining active session. Both child exit success and an explicit completion marker are required; the marker alone cannot bypass database assertions.

## Running locally

Prerequisites: Node 24 on PATH, the SDK pinned by backend/global.json, the existing local Mongo replica set, frontend/legacy dependencies, and an installed browser runtime. Run the install/build steps from the repository root:

    npm ci --prefix frontend/tests/browser --ignore-scripts
    node frontend/tests/browser/node_modules/playwright/cli.js install --with-deps chromium
    npm run build --prefix frontend

For PowerShell, from the repository root:

    $env:UNITTOKO_STOCK_BROWSER = '1'
    $env:UNITTOKO_BROWSER_NODE = (Get-Command node).Source
    $env:UNITTOKO_BROWSER_PLAYWRIGHT_PACKAGE = (Resolve-Path 'frontend/tests/browser/node_modules/playwright/package.json').Path
    $env:UNITTOKO_BROWSER_CHANNEL = 'chromium'
    Push-Location backend
    try {
        dotnet test tests/UnitToko.Tests/UnitToko.Tests.csproj --filter 'FullyQualifiedName~StockCountBrowserTests' --logger 'trx;LogFileName=stock-browser.trx'
    } finally {
        Pop-Location
    }

Set UNITTOKO_BROWSER_CHANNEL to msedge to use an installed Edge browser on Windows. Chromium uses the driver pinned by frontend/tests/browser/package-lock.json (Playwright 1.62.1). PLAYWRIGHT_BROWSERS_PATH may point to an isolated local runtime cache; use the same value for installation and execution. No real credentials are required.

The normal backend test run explicitly skips this opt-in browser case. GitHub's separate Browser, HTTPS and real Mongo job installs its pinned tools, builds the frontend, starts its own replica set and enables the case. It parses the result file and requires exactly one executed/passed test, zero failures and zero unexecuted cases, preventing a missing filter or disabled opt-in from reporting success. The CI step runs from backend so global.json selects the intended SDK.

## Limits

This covers the local synthetic-provider boundary, not actual Auth0 policies or external event delivery, public certificate/hosting configuration, network isolation in a deployed environment, cross-browser coverage beyond the tested Chromium family, every role/workflow, realistic capacity, independent implementation review or a branch pilot. The broader identity and production acceptance gates remain open.
