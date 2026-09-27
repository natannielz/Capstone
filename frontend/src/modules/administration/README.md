# Access administration interface

Design before implementation: a working approval desk for Unit Toko administrators and designated business approvers. The left column keeps the pending proposal queue visible; the wider right column shows the selected proposal or a new access request. A full-width account lookup opens approved memberships and existing grants before a proposal can be previewed. Mobile stacks the queue above the working area without hiding approval context.

```text
Kelola akses                                     Muat ulang
Account reference / authorized account summary
Antrian pengajuan        | Detail / Pengajuan akses
pending rows             | membership + scope + role/packs
previous / next          | exact permissions + dates + reason
                         | server preview / submit for approval
```

Tokens: teal `#087f8c` actions, ink `#1d3640` text, white `#ffffff` work surfaces, background `#f2f6f7`, border `#d7e3e7`, orange `#ed7829` keyboard focus. Segoe UI is the body/control font; Trebuchet MS is for page headings. Left-aligned labels, 16px body text, 14px supporting text, 24–32px main heading. No global CSS, decorative cards, animation, or invented account names. Borders group real tasks; a stronger teal selection rule identifies the active queue row.

Brief review: a generic metrics dashboard would distract from who gains which duties and who must approve. This layout instead keeps subject reference, exact scope, proposed permissions, effective dates and independent approval visible together. The UI describes actions in Indonesian and shows account/scope references honestly until the verified directory supplies names.

Behavior: same-origin generated API client, server-provided action permissions, immutable string IDs/versions, current server preview required for submission, independent approval, retained mutation idempotency keys on retry, stale-conflict refresh, explicit unknown-outcome recovery. No role checks in browser code and no legacy authentication bridge.

Unknown-outcome retries retain their exact request body, resource version and key while this page remains open. Editing and application navigation are disabled until confirmation; a browser-unload warning protects accidental reloads. The component does not persist sensitive approval drafts or reasons in browser storage. A forced browser close still requires operational reconciliation, and is not claimed as an implemented cross-session recovery flow.

The full proposal scope, subject/requester, membership target, template version, effective/expiry dates (including no expiry), transaction permissions and delegated authority (including none) remain visible in the independent approval confirmation. Names and directory lookup remain a T03 integration dependency; account references are not converted to invented identities.
