# Identity frontend

`/identity/login` and `/identity/account` use the ASP.NET identity API through one public origin. The same origin must route `/api/v1/*` and `/auth/*` to the backend; these pages do not activate or fall back to the legacy demo login.

- Session display is validated against the generated DTO; account identifiers stay strings. The browser receives no provider tokens or session IDs.
- Login, invitation acceptance, step-up and logout obtain a fresh antiforgery token and submit a native URL-encoded POST. Provider redirects use full-document navigation. Invitation codes only enter an explicit password-style field and the POST body; no query parameters or persistent storage.
- The shared API transport obtains a fresh token for each unsafe request. Failed preparation dispatches no business request. The operation ledger distinguishes that known failure from an already uncertain earlier request; mutations are never retried automatically.
- Account navigation uses the current access API. `stepUpOperations` only offers eligible challenges and never authorizes a business write. Local timeout display does not poll or silently renew sessions.
- Identity documents use `Referrer-Policy: same-origin` metadata so native same-origin POST keeps its `Origin` while external provider navigation receives no referrer. API callback responses can separately use `no-referrer` to protect authorization response parameters.

Provider forms currently retain backend problem responses on native navigation errors. Account recovery copy directs the user to the approved operator process; there is no claim that reset dispatch or recovery completion is available. Invitation administration, provider dispatch, live-tenant acceptance and production commerce remain separate work.

Meaningful coverage lives in `tests/identity-csrf-client.test.ts` and `tests/identity-workflow.test.ts`, alongside existing administration contract tests. Browser fixture evidence in `tmp/production/identity-browser-result.json` explicitly distinguishes real browser behavior from synthetic API/provider responses.
