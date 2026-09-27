# Module boundaries during the T01 transition

The repository has three main source directories: frontend, backend, and docs. The current Next.js routes are compatibility adapters while the C# service is built. The TypeScript legacy service under backend/legacy remains the only writer for existing demo stock and money until the coordinated cutover.

| Owner | Files | Responsibility |
| --- | --- | --- |
| Frontend routing | frontend/src/app, frontend/src/components, frontend/src/hooks | Pages, browser interactions, thin API adapters |
| Frontend view layer | frontend/src/lib/domain, frontend/src/lib/client, frontend/src/lib/utils.ts | Temporary public DTO shapes, role labels, display projections, local cart/draft state |
| Legacy domain authority | backend/legacy/domain/engine.ts, seed.ts, accounts.ts, buyers.ts, selectors.ts, model.ts, clock.ts | Commands, server authorization/scoping, fixture state, domain errors, stock and finance rules |
| Legacy infrastructure | backend/legacy/server, db, drizzle | HTTP helpers, persistence, uploads, documents, SQL schema and migrations |
| New service | backend/src, backend/tests, backend/contracts | C# API, worker, modules, contract and integration tests as subsequent tasks implement them |

Frontend view modules mirror a few legacy state types and read-only arithmetic functions because the existing UI consumes whole scoped state. This is a temporary compatibility contract, not a second command or authorization implementation. In particular, frontend selectors do not export scopeState, allowed, ownDivision, canReadAttachment, or attachmentContext; frontend buyers do not export assertBuyerAccess or buyerRefForActor. The server evaluates those functions from backend/legacy. Browser bundles must never import backend/legacy directly. Frontend lint runs scripts/check-boundaries.mjs to enforce this boundary.

Next API routes under frontend/src/app/api import backend/legacy through the @legacy alias. Page sessions stay in frontend/src/lib/server and call the legacy HTTP helper. The alias @ points to frontend/src. Neither alias is an external API contract. Migration files are resolved from backend/legacy/drizzle and included in the Next server output trace. The legacy package declares its own runtime dependencies so clean deployment does not depend on a root node_modules folder. With two physical installations, Next bundles the legacy copy of native libSQL by default; the Node server webpack config externalizes @libsql/client to the frontend runtime copy and fails the build if its @libsql/client or libsql lock version differs from backend/legacy. API traces include the platform native binary and all legacy SQL migrations.

Test ownership manifest:

| Kind | Tests and helpers | Next owner |
| --- | --- | --- |
| UI and view | catalog-basket-v9, catalog-v6, customer-account-v14, customer-account-v9, customer-action-draft-v7, customer-billing-v7, customer-cart-v7, customer-navigation-v7, customer-profile-v15, customer-views-v7, dashboard-v14, invoice-selection-v6, login-v6, native-history-v15, navigation, operations-list-v15, operations-views, payment-views-v15, procurement-remainder-v11, shop-family-selection | frontend/tests |
| Domain rules | customer-domain-v7, domain-v6, domain, finance-decisions-v9, order-attachments-v9, rbac, receipt-evidence-v11, requests-v6 | backend/legacy during port; frontend/tests currently runs them against backend/legacy |
| HTTP and session | access-portals-v8, admin-password-reset-v9, attachment-upload-v7, logout-v6, operations-recovery-v9, test-environment | legacy compatibility route tests under frontend/tests |
| Infrastructure and fixture | customer-repository-v7, demo-catalog-v10, repository, cloudflare-stub.ts | legacy SQL/seed regression under frontend/tests |
| Golden replay | backend/tests/legacy-golden.test.ts and backend/tests/Fixtures/legacy-scenarios.json | backend/tests, invoked by frontend/scripts/test-domain.mjs |

The tests remain together in frontend/tests for the transition so npm test exercises the previous regression suite and the backend legacy code from one entry point. The golden replay runs as a separate backend test bundle after the baseline and future-date repository checks. As C# modules replace legacy authority, move their domain and infrastructure cases to backend/tests and retain frontend coverage for views and adapters.

Local installation: run npm ci --prefix backend/legacy, then npm ci --prefix frontend from the repository root. Run npm --prefix frontend run typecheck, lint, test, and build. The Node runtime is 24.x. Vercel's planned project root is frontend; its install command installs both packages. This is configuration only and does not change the live deployment.
