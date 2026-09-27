# Organization administration

This workbench manages reviewed organization and unit changes. The authoritative hierarchy stays in authorization scopes; missing legacy names remain explicitly unavailable until an independently approved profile is supplied.

## Design before implementation

Use the existing teal `#087f8c`, ink `#1d3640`, white `#ffffff`, background `#f2f6f7`, muted text `#4d6570`, and orange focus `#ed7829`. Segoe UI carries forms and operational content; Trebuchet carries headings. Align all content left. Reuse the shared administration navigation and controls.

The layout is a unit/proposal register beside a detailed review sheet. The identifying code and parent relation lead each row. A reactivation review places its four impact counts together above the decision reason, because restoring access is the consequential action here. On narrow screens the register precedes the detail. Avoid animation, decorative cards, or invented organization names.

```
Organisasi dan unit                         Refresh
Administration navigation / recovery
Units | Proposals       Selected record / proposal
reference and parent    Before → requested details
status                  Reactivation impact
pagination              Reason + explicit decision
```

Review against the brief: keep existing administrative styling, rather than introduce a dashboard of summary metrics. The impact counts belong only in the specific reactivation proposal; they are not decorative page statistics. Names and codes are editable; hierarchy kind and parent remain visible and immutable for existing records.

## Safety and limits

Server `allowedActions` controls actions. IDs and versions remain strings. Commands use the shared immutable-request ledger, durable reference journal, expected-actor header, and operation receipt recovery. No credentials or full requests are stored in browser storage. A changed current account remounts account-owned forms and state. Uncertain outcomes lock new changes until reconciliation. The production session remains unavailable until T03 provider integration; no test authentication bridge exists here.

Reactivation counts are conservative potential access records, not a claim that every counted account can authenticate. The server independently checks fresh MFA, maker/checker independence, affected-beneficiary independence, ancestry, revisions, and impact changes before approval. Existing suspended descendants stay suspended. Future large-organization usage may need a paged impact-analysis job; current reads and commands run as transactional reviewed operations.
