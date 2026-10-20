# Back-office / B2B Ordering and Operations UX Patterns (as of Oct 2026) for Unit Toko

Scope: patterns engineers can apply to Unit Toko workspaces (catalog, cart/checkout, order review/approval, substitution consent, fulfillment, payments review desk, returns, procurement, inventory, reports, administration). Sources fetched directly unless marked "(via search summary)". Note on access: SAP Fiori pages (sap.com/design-system, experience.sap.com) returned HTTP 403 to direct fetch, so SAP content below comes from search-engine summaries of those pages. The legacy Polaris React docs (polaris-react.shopify.com) now redirect to shopify.dev "App Home" Polaris web-component docs.

## 1. Data-dense tables and lists (columns, filters/saved views, sorting, pagination, row and bulk actions, density, sticky headers, mobile)

### Takeaway
Mature systems agree on a fixed table setup: a toolbar (search, filters, display settings, primary action) above the table; row checkboxes with a three-state "select all"; a batch-action bar that replaces the toolbar when rows are selected; row actions as inline icons when there are fewer than 3, otherwise an overflow menu; sortable headers; frozen headers and key column; numbers right-aligned; set row heights (default about 40px, compact 32px, extra-small 24px); and pagination or "Load more" rather than pure infinite scroll for work lists. On mobile, lock the identifying column, show clearly that more columns exist, and let users choose which columns they see.

### Cited Findings
**Anatomy and toolbar**
- Carbon: "The data table toolbar gives a location for primary buttons, search, filtering, table display settings, and other utilities." Column headers sort ascending/descending; rows can expand; single or batch actions can be taken on rows. — [Carbon Data table](https://carbondesignsystem.com/components/data-table/usage/)
- Carbon: put data tables in the page's main content area with enough room to show data without truncation. Avoid tables inside tables or small containers. — [Carbon Data table](https://carbondesignsystem.com/components/data-table/usage/)
- Shopify index pattern: the index layout lets users manage many objects in a table, with filter, sort, quick actions and bulk actions (e.g., delete products, pause/activate campaigns). Resource index pages should be full-width so they fit many columns. Show row actions only when a row is hovered or selected, to avoid clutter. Provide breadcrumbs/Back rather than relying on the browser button. (via search summary) — [Shopify App Home index pattern](https://shopify.dev/docs/api/app-home/patterns/templates/index); [Shopify resource index](https://shopify.dev/docs/api/app-home/patterns/templates/resource-index)

**Selection, row actions, bulk actions**
- Carbon: row checkboxes have two states (checked, unchecked). The header "check all" has three (checked, unchecked, indeterminate). — [Carbon Data table](https://carbondesignsystem.com/components/data-table/usage/)
- Carbon batch actions: "Once an item from the table is selected, the batch action bar appears at the top of the table". "When batch mode is active, single action icons and overflow menus on the row should be disabled." Users leave batch mode with Cancel (far right of the bar) or by deselecting all. — [Carbon Data table](https://carbondesignsystem.com/components/data-table/usage/)
- Carbon inline actions: each row has an overflow menu of row-specific actions. "When the overflow menu contains fewer than three options, keep the actions inline as icon buttons instead." — [Carbon Data table](https://carbondesignsystem.com/components/data-table/usage/)
- Carbon expandable rows: use them for supplementary information or data that needs extra query time. If the expanded content feels cramped, send the user to a dedicated page, side panel or another table. — [Carbon Data table](https://carbondesignsystem.com/components/data-table/usage/)
- NN/g: data tables must support four tasks: find records that fit criteria, compare data, view/edit/add a single row, and take action on records. — [NN/g Data Tables: Four Major User Tasks](https://www.nngroup.com/articles/data-tables/)

**Sorting, scanning aids, density**
- Carbon sorting has three states (unsorted, ascending, descending). Only the sorted column shows an icon. Unsorted icons appear on hover. — [Carbon Data table](https://carbondesignsystem.com/components/data-table/usage/)
- Carbon: keep the row hover state on, because it helps users scan a row even when the row is not interactive. A zebra-stripe modifier is available for horizontal scanning. — [Carbon Data table](https://carbondesignsystem.com/components/data-table/usage/)
- NN/g: "Freeze header rows and header columns (if the table is larger than the screen)". Borders, zebra striping and hover highlighting help users keep their place. — [NN/g Data Tables](https://www.nngroup.com/articles/data-tables/)
- NN/g: hiding and reordering columns must be low-effort, discoverable, and possible without drag and drop. A clear indicator should show that some columns are hidden. — [NN/g Data Tables](https://www.nngroup.com/articles/data-tables/)
- Carbon row/header heights: XS 24px, SM 32px, MD 40px (default), LG 48px, XL 64px. The header always matches row size. XL only when rows hold 2 lines. Pagination height matches row height. — [Carbon Data table](https://carbondesignsystem.com/components/data-table/usage/)
- SAP Fiori content density: "cozy" sets a 2.75rem (44px) touch target and suits touch devices. "Compact" shrinks rows and toolbars to 2rem (32px) without changing font size and suits mouse/keyboard. Choose density by device. Never mix cozy and compact on one page. Set density at app level. Hybrid-device users can switch in personalization, and the choice persists. (via search summary) — [SAP Fiori Content Density](https://www.sap.com/design-system/fiori-design-web/v1-136/foundations/visual/cozy-compact)
- GOV.UK: "When comparing columns of numbers, align the numbers to the right in table cells" (uses the `govuk-table__header--numeric` / `__cell--numeric` classes). — [GOV.UK Table](https://design-system.service.gov.uk/components/table/)

**Filtering**
- Carbon: use **instant** filtering when the user picks from one category or makes a single selection. Use **batch** filtering with an "Apply filters" button when users make several selections across categories, or when data returns slowly. — [Carbon Filtering](https://carbondesignsystem.com/patterns/filtering/)
- Carbon: each category needs a way to clear all its applied filters at once. When several categories are applied there should also be a way to dismiss all filters across categories. — [Carbon Filtering](https://carbondesignsystem.com/patterns/filtering/)
- NN/g: results pages that refresh too soon or shift the page position disrupt filtering. — [NN/g User Intent Affects Filter Design](https://www.nngroup.com/articles/applying-filters/)
- Jira Service Management: queues are saved, named filters with chosen columns. They can be grouped into priority groups and are often sorted by SLA. — [JSM: What are queues?](https://support.atlassian.com/jira-service-management-cloud/docs/what-are-queues/)

**Pagination vs infinite scroll**
- Atlassian dynamic table has pagination built in. When rows exceed the per-page maximum, a pagination component appears at the end of the table. — [Atlassian Dynamic table](https://atlassian.design/components/dynamic-table/usage)
- NN/g: infinite scrolling removes landmarks, so users struggle to re-find items. It is especially bad when the list position isn't saved after a user opens a detail page and comes back ("pogo-sticking"). Without a Load More button it can make the footer unreachable. For some sites "pagination or a Load More button will be a better solution." — [NN/g Infinite Scrolling](https://www.nngroup.com/articles/infinite-scrolling-tips/)

**Mobile adaptation**
- NN/g: make large tables usable on mobile by locking headers and letting users pick the subset of data they need. If horizontal scrolling is needed, make it obvious: arrows or cut-off columns work better than dots. Lock the leftmost (row-header) column in place. — [NN/g Mobile Tables](https://www.nngroup.com/articles/mobile-tables/)

### Inferences
- Unit Toko needs one shared `DataTable` contract for all list workspaces (orders, dispatch, payment review, stock count, procurement): toolbar with search, filter and column settings; row checkbox plus a three-state header checkbox; batch bar showing "N selected · [actions] · Cancel"; row actions disabled in batch mode; numeric/currency columns right-aligned with tabular figures; sticky header; first column (order no. / SKU code) sticky.
- Default to **compact (32px)** rows for desktop staff workspaces (Kepala Toko, billing, accounting) and **cozy (40–48px)** for touch flows (Staf Toko picking/receiving on tablets/phones). Make density a per-user persisted setting, but never mix densities on one page (SAP rule).
- Use batch filters with an "Apply" button for wide report and accounting tables, where queries are slow. Use instant filters for single-facet quick tabs ("Menunggu persetujuan", "Hari ini").
- Use pagination, or "Load more" with the list position kept, for work queues. Avoid pure infinite scroll, because staff go back and forth between list and detail all day.
- Saved views: implement them as named, URL-addressable filter + sort + column sets (as JSM queues do). Ship role defaults, e.g., billing: "Bukti bayar perlu ditinjau", "Dana titipan belum dialokasikan".
- Bulk actions must still follow the "review before decide" rule: a batch approve or dispatch opens a review summary that lists each item's outcome and any items excluded as ineligible (e.g., the user's own submissions).
- Mobile: for Staf Toko picking/dispatch, use one card per row with the key identifier, status and quantity, and keep the full table behind "Lihat detail". For report tables, keep a horizontally scrollable table with a sticky first column and a visible cut-off.

### Gaps
- I could not fetch SAP Fiori's responsive table and variant-management (saved views) pages (HTTP 403), so I have no primary SAP guidance on saved-view UX such as "default view", "public vs private views" or "modified" indicators.
- The Fluent 2 table usage URL returned 404. I found no current Microsoft Fluent table guidance.
- The current Shopify Polaris web-component docs don't document IndexTable's condensed/mobile behavior in the content I fetched. Polaris row-action-on-hover guidance came via search summary only.
- I found no quantitative studies on the best rows-per-page for operational tables.

## 2. Work queues / inboxes for multi-role operations (unified "my work", prioritization, SLA/aging, assignment/claiming, counts, notifications)

### Takeaway
Effective work queues are saved filters sorted by urgency (SLA closest to breach first), with a visible clock or aging indicator per item. They offer a small set of keyboard-fast triage actions (accept / decline with reason / snooze / mark duplicate), make ownership explicit (assigned-to, rotation, "who's on triage"), and keep "needs my action" separate from FYI notifications (Linear's Priority tab).

### Cited Findings
- JSM: queues are often sorted by an SLA. "a clock on the work item indicates the time until your team's next target is due." An example "SLA priority" queue puts requests closest to expiring on their goal at the top. Separate queues can serve each team, department or locale. — [JSM: What are queues?](https://support.atlassian.com/jira-service-management-cloud/docs/what-are-queues/)
- JSM: when you create a queue you choose its name, which requests are filtered in, and which columns show. Queues can be sorted into priority groups. — [JSM: What are queues?](https://support.atlassian.com/jira-service-management-cloud/docs/what-are-queues/)
- Linear Triage is "a special inbox for your team" for issues created by other teams or integrations. Actions: accept (shortcut 1), decline (2), mark as duplicate (3 or MM), snooze (H). Accept offers a comment and moves the item to the default status. Decline moves it to Canceled with an optional explanatory comment. Duplicate merges it into a canonical issue. Snooze hides it until a chosen time or new activity, whichever comes first, and hides it from other users by default. — [Linear Docs: Triage](https://linear.app/docs/triage)
- Linear triage responsibility: specific members can be notified of new issues or auto-assigned, rotation can sync from on-call schedules, and team members can see who is currently assigned to monitor triage. — [Linear Docs: Triage](https://linear.app/docs/triage)
- Linear Inbox: users are auto-subscribed to items they create, are assigned or are mentioned in. The **Priority** tab "separates notifications that need your attention from other updates". Display options include "Show snoozed", "Show unread first", priority filter, badge count and grouping. — [Linear Docs: Inbox](https://linear.app/docs/inbox)
- GitHub uses a personal "my work" search: `review-requested:[USERNAME]` or `team-review-requested:[TEAMNAME]` finds pull requests awaiting your review. — [GitHub Docs: Approving a PR with required reviews](https://docs.github.com/en/pull-requests/collaborating-with-pull-requests/reviewing-changes-in-pull-requests/approving-a-pull-request-with-required-reviews)
- Salesforce Omni-Channel (push routing): agents accept or decline assigned work items, and a decline records a DeclineReason. After a decline or a missed acceptance timeout, the item is not routed to that agent again. Capacity models stop new work once an agent hits 100% capacity. (via search summary of older API versions; check the current version) — [Salesforce AgentWork object (API v230)](https://developer.salesforce.com/docs/atlas.en-us.230.0.api.meta/api/sforce_api_objects_agentwork.htm)
- NN/g: operational dashboards "aim to impart critical information quickly to users as they are engaged in time-sensitive tasks". Analytical dashboards support analysis without the same time pressure. — [NN/g Dashboards](https://www.nngroup.com/articles/dashboards-preattentive/)
- Carbon status indicators: "High attention" indicators are for situations needing immediate action (alerts, exceptions, confirmations, errors). "Medium attention" is for situations needing no immediate action (acknowledgements, progress). — [Carbon Status indicators](https://carbondesignsystem.com/patterns/status-indicator-pattern/)

### Inferences
- Build a cross-module **"Pekerjaan saya" (My work)** home that aggregates items needing the current user's action across modules, using the server-side authorization the app already has (items the user can act on, not just view). Group it by action type ("Setujui pesanan", "Tinjau bukti bayar", "Konfirmasi substitusi", "Terima barang"). Each group shows a count and opens the module's filtered queue.
- Sort every queue by "due soonest / oldest waiting" by default. Show aging as relative time plus an absolute timestamp on hover ("Menunggu 2 hari"), and raise it to a high-attention color only past a defined threshold (e.g., an approval-SLA breach).
- Keep "action required" separate from "FYI" notifications (Linear Priority vs Other). Badge counts should count only action-required items.
- For shared queues (billing review desk, Kepala Toko order acceptance), add explicit **claim/assign** ("Ambil", "Ditangani oleh Rina") to prevent two people reviewing the same proof. Combine it with the optimistic-concurrency conflict UX in section 5.
- Decline/reject actions should always capture a reason (Linear, Salesforce), and that reason should show in the requester's timeline.
- Add keyboard shortcuts to triage-heavy desks (payment review, order review), e.g., J/K next/previous, A approve-review, R reject-with-reason. Show them inline (see section 6).

### Gaps
- I couldn't retrieve primary ServiceNow Workspace or Salesforce Lightning (SLDS) design guidance on list/queue UX. The Salesforce material is API-level and partly third-party.
- The JSM SLA page URL I tried returned 404, so I have no primary detail on how SLA clocks show "breached/paused" states.
- I found no research quantifying unified-inbox vs per-module-queue performance.

## 3. Approval and maker-checker UX (review summaries, diffs, reason capture, self-approval prevention, confirm vs undo, step-up auth)

### Takeaway
Show a check-your-answers style summary before committing, with change links that return to the summary. Name the exact consequence in the dialog title and button ("Approve order PO-123 · Rp 4.250.000"), never "Are you sure?". Keep confirmations for consequential or irreversible actions only. Invalidate an approval when the reviewed content changes (GitHub's "dismiss stale approvals"). For step-up authentication, preserve and restore the user's in-progress action.

### Cited Findings
**Review before decide**
- GOV.UK Check answers: let users check answers before submitting. Give a "Change" link next to each section, with visually hidden text naming what is changed. Pre-populate previous answers. After a change, "Continue" returns the user to the check answers page without repeating the journey. If a change triggers new questions, ask them before returning. Show skipped optional answers as "Not provided". — [GOV.UK Check answers](https://design-system.service.gov.uk/patterns/check-answers/)
- GOV.UK Summary list: when information is missing, put a link to the question page in the value column instead of a "Change" link. — [GOV.UK Summary list](https://design-system.service.gov.uk/components/summary-list/)
- GOV.UK Confirmation pages must include a reference number (if any), what happens next and when, contact details, links to likely next tasks, a feedback link, and a way to save a record (e.g., PDF). — [GOV.UK Confirmation pages](https://design-system.service.gov.uk/patterns/confirmation-pages/)

**Confirmation dialogs vs undo**
- NN/g: use confirmation dialogs before actions with serious consequences (destroying work, large amounts of money), especially irreversible ones, but "do try your best to offer undo". Do not use them for routine actions, because overuse makes people stop reading them. Be specific about the consequence rather than asking "Are you sure you want to do this?". Replace Yes/No with labels that summarize the outcome (e.g., "Delete file" / "Keep file"). Use progressive disclosure for more detail. — [NN/g Confirmation Dialogs](https://www.nngroup.com/articles/confirmation-dialog/)
- Carbon: modal variants are passive, transactional (cancel + primary), acknowledgment (single OK) and progress (multi-step). To confirm a decision, "Both the title and the button should reflect the action that will occur". Destructive or irreversible actions use a "transactional danger modal". "One modal should never trigger another modal". If a task needs a confirmation modal, the task itself shouldn't be in a modal. — [Carbon Dialogs](https://carbondesignsystem.com/patterns/dialog-pattern/)
- NN/g: use modals for important warnings that prevent critical errors. Show form errors on the page next to the field rather than in a modal. Avoid modals that interrupt high-stakes flows such as checkout. — [NN/g Modal & Nonmodal Dialogs](https://www.nngroup.com/articles/modal-nonmodal-dialog/)

**Showing what changed / approval validity**
- GitHub: reviewers review the diff in the "Files changed" tab before approving. "If a pull request you approved has changed significantly, you can dismiss your review. The pull request will need a new review before it can be merged." — [GitHub Docs: Approving a PR](https://docs.github.com/en/pull-requests/collaborating-with-pull-requests/reviewing-changes-in-pull-requests/approving-a-pull-request-with-required-reviews)
- GitHub branch protection offers "Dismiss stale pull request approvals when new commits are pushed" and "Require approval of the most recent reviewable push". Approvals are also dismissed as stale if the merge base introduces new changes after review. Required approvals can be restricted to people with write permission or designated code owners. Merging with pending or rejected reviews returns an explicit error. — [GitHub Docs: About protected branches](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches)

**Step-up authentication without losing context**
- WorkOS AuthKit: re-authentication redirects the user out of the app, so "preserve the original location and any in-progress action with the `state` parameter" and restore context so the user can finish the high-value action. Avoid `max_age` under 60 seconds, which forces near-immediate re-auth. — [WorkOS Reauthentication](https://workos.com/docs/authkit/reauthentication)
- USENIX SOUPS 2016 study: users can find the default, non-transparent re-authentication prompt annoying. Participants preferred more secure, non-transparent configurations for sensitive apps (e.g., banking) and more convenient ones for low-sensitivity apps. (via search summary) — [Agarwal et al., SOUPS 2016](https://usenix.org/conference/soups2016/technical-sessions/presentation/agarwal)
- Practitioner guidance: trigger step-up only when risk rises or for sensitive actions (billing changes, account deletion), keep the rest of the session intact, and enforce on the server, since UI-only step-up lets an old session perform the sensitive operation. (via search summary; secondary sources) — [SuperTokens: Step-up auth](https://supertokens.com/blog/step-up-auth); [Gravitee: Step-up authentication](https://www.gravitee.io/step-up-auth.html)

### Inferences
- Use one **Review panel** pattern for every decision (approve spending, accept order, consent to substitution, verify payment, allocate suspense funds, approve return/refund, grant/delegate access). It contains a summary list of what will be committed, a diff block ("Sebelum → Sesudah", with changed values highlighted, e.g., substituted SKU, qty, price, total), consequence text, a required reason field for reject/return-for-revision, and one primary button labelled with the verb and object ("Setujui pesanan PO-2026-0142").
- Because Unit Toko already requires review-before-decide, don't stack a second "Are you sure?" modal on top of the review panel (Carbon: no nested modals). The review panel is the confirmation. Reserve a danger-style confirmation for irreversible money movements (refund issued, payment allocation, write-off).
- Prevent self-approval in a way the user can see, not just with a server rejection. On the user's own submission, replace the Approve button with an explanation ("Anda mengajukan pesanan ini, jadi persetujuan harus oleh pejabat lain: Kepala Unit / delegasi."). In bulk approval, list the user's own items as "dilewati — pengaju sendiri".
- Apply GitHub's stale-approval rule. If the order changes after a checker opened the review (substitution, qty edit, price change), show "Pesanan berubah sejak Anda membuka ulasan" with the diff, and require a fresh review before Approve is enabled. This pairs with the server's concurrency check.
- Step-up (MFA) for high-risk decisions should open as an in-place dialog over the review panel. Keep the reason text, selected items and idempotency key, then resubmit the same original request after verification. If a redirect is unavoidable, carry the in-progress action in `state` and restore the review panel exactly.
- After a decision, show a confirmation page or inline panel with the reference number, what happens next and who acts next ("Menunggu verifikasi Koperasi"), and a link to the timeline (GOV.UK confirmation pattern).

### Gaps
- I didn't retrieve GitHub's explicit "authors cannot approve their own pull request" wording from the fetched pages. The self-approval guidance above is inferred from the stale-approval and most-recent-push settings.
- I found no design-system guidance focused on financial maker-checker (four-eyes) UI, e.g., Fiori My Inbox approvals, which was blocked with HTTP 403.
- I found no primary research on diff presentation for business-record approvals, as opposed to code diffs.

## 4. Status and timeline UX (vocabularies, steppers vs activity timelines, cross-module history)

### Takeaway
Use a short, controlled set of status labels with a consistent color/shape/icon meaning. Show the current status prominently, with the next step and who owns it. Give a chronological activity history underneath, updated often enough that long silences don't erode trust. Use a stepper only for predictable linear lifecycles (order to delivery), and an activity timeline for full cross-module history.

### Cited Findings
- NN/g: a status tracker (pull) "usually consists of the latest status and prior updates, displayed in chronological order". Progress updates (push via email/SMS) are sent when status changes and should link to the tracker. — [NN/g Status Trackers and Progress Updates](https://www.nngroup.com/articles/status-tracker-progress-update/)
- NN/g: "when there are long periods with no update, users start to think perhaps something went wrong, they lose trust in the status tracker". HM Passport Office's fine-grained updates such as "Joined the processing queue" reassure users. USCIS's months-long gaps, and a "Next steps" panel that was no longer relevant, are shown as anti-patterns. — [NN/g Status Trackers](https://www.nngroup.com/articles/status-tracker-progress-update/)
- NN/g guideline 1: let users find tracking information from their account. If you embed a tracker, link to the carrier's tracker. — [NN/g Status Trackers](https://www.nngroup.com/articles/status-tracker-progress-update/)
- Atlassian Lozenge: "a prominent, compact label used to communicate a meaningful attribute that affects how people understand, prioritize or act on an object". Lozenges are non-interactive; for an inline status switcher, use a "lozenge dropdown trigger". Use sentence case. — [Atlassian Lozenge](https://atlassian.design/components/lozenge/usage)
- Carbon status indicators define severity tiers (high attention: immediate action; medium: no immediate action). Shape-only indicators need outlines and paired text for screen readers and low color vision, especially orange/yellow. Status icons are standardized so meanings stay consistent across products. — [Carbon Status indicators](https://carbondesignsystem.com/patterns/status-indicator-pattern/)
- GOV.UK Task list statuses include "Completed" and "Incomplete" (blue tag). The status shows whether a task can be started. Users can do tasks in any order and move on only when all are completed. — [GOV.UK Task list](https://design-system.service.gov.uk/components/task-list/)

### Inferences
- Define a **status dictionary** per aggregate (Order, Shipment/Dispatch, Payment, Return, Purchase order) with: code, Indonesian label (1–3 words, sentence case), tone (neutral / in progress / success / warning / danger), icon, "who acts next", and plain-language description. Render statuses only from this dictionary so the same word never means different things in different modules. Example tones: "Menunggu persetujuan" = in progress, "Ditolak" = danger, "Selesai" = success, "Perlu tindakan Anda" = warning (high attention).
- Status chips must carry text plus icon, never color alone (Carbon accessibility).
- On order detail, put a **horizontal stepper** of the happy-path lifecycle (Diajukan → Disetujui → Diterima toko → Disiapkan → Dikirim → Diterima divisi → Ditagih → Lunas) above a **vertical activity timeline** that merges events from ordering, fulfillment, payments and returns. Each event shows actor, role, timestamp, event, reason/comment, and links to the related record (dispatch note, invoice, payment proof). Branch states (substitution pending consent, partial dispatch, payment rejected) show as exceptions on the stepper, not extra linear steps.
- Add fine-grained "in queue" events ("Masuk antrean verifikasi Koperasi") so PICs don't see long silent gaps (NN/g HMPO example). This could reduce "where is my order?" calls; I have no data on that.
- Include sealed/verified-checkpoint provenance in the timeline as a subtle "terverifikasi" marker, without exposing technical hashes by default.

### Gaps
- I found no authoritative, current (2024–2026) research comparing steppers with timelines for B2B order tracking. The NN/g article dates from 2019.
- The full 16 NN/g guidelines weren't all extractable from the indexed text; only guideline 1 and the general principles are cited.

## 5. System states (empty, loading, partial failure, permission denied, offline, conflict, unknown outcome) and idempotent retry

### Takeaway
Every state needs (a) a plain-language reason, (b) what happened to the user's data, and (c) the next action. Use skeletons for full-view loads under about 10s, spinners for single modules taking 2–10s, and determinate progress for anything over 10s. Never block the whole UI on a network call. For conflicts, keep the user's input and show what changed. For a lost response, retry with the **same** idempotency key, which returns the original result rather than creating a duplicate.

### Cited Findings
**Empty states**
- Carbon defines three empty-state types. "No data" (first use) explains what will appear and the next step. "User action" (e.g., no search results) suggests adjusting search or filters. "Error management" covers data that exists but can't be shown: for a permissions issue, "Suggest steps or process to request access"; for a systems issue, explain how to learn more (e.g., an activity log); when configuration is required, explain it and give the first step. — [Carbon Empty states](https://carbondesignsystem.com/patterns/empty-states-pattern/)
- Carbon empty-state anatomy: optional image, title (write it positively where possible, e.g., "Start by adding data assets" rather than "You don't have any data assets"), body explaining the next action, optional primary action, optional secondary link. Keep words to a minimum. Use plain language with no codes. Supplementary text is unnecessary when nothing needs attention (e.g., no alerts triggered). — [Carbon Empty states](https://carbondesignsystem.com/patterns/empty-states-pattern/)
- Shopify example empty state: heading "No products yet", subheading explaining the purpose, primary "Create product", secondary "Browse products". Success shows as a toast ("Product created"). — [Shopify resource index](https://shopify.dev/docs/api/app-home/patterns/templates/resource-index)

**Loading**
- NN/g: spinners suit waits of 2–10 seconds. Skeleton screens should be used for waits under 10 seconds. Anything over 10 seconds needs a progress bar or an explicit duration estimate. Spinners suit a single module; skeletons suit a full-screen load. — [NN/g Skeleton Screens 101](https://www.nngroup.com/articles/skeleton-screens/)
- NN/g: for anything under 1 second a looped animation is distracting. Don't use looped animation for actions over 10 seconds. — [NN/g Progress Indicators](https://www.nngroup.com/articles/progress-indicators/)
- Carbon: data tables use skeleton states instead of spinners when extra load time is expected. Progressive loading shows structure first, then batches of content, which helps pages that pull data from several sources. — [Carbon Data table](https://carbondesignsystem.com/components/data-table/usage/); [Carbon Loading](https://carbondesignsystem.com/patterns/loading-pattern/)

**Errors, outages, partial failure**
- GOV.UK "problem with the service" pages: H1 "Sorry, there is a problem with the service", then "Try again later.", then what has happened to the user's answers mid-transaction (example: "We saved your answers. They will be available for 30 days."), plus contact info or an alternative service. Don't use breadcrumbs, jargon like "500 or bad request", "We are experiencing technical difficulties", or red warning text. Store entered information so users can resume. — [GOV.UK Problem with the service pages](https://design-system.service.gov.uk/patterns/problem-with-the-service-pages/)
- NN/g error messages: "Preserve the user's input. Let users correct errors by editing their original action instead of starting over." — [NN/g Error-Message Guidelines](https://www.nngroup.com/articles/error-message-guidelines/)
- Carbon notifications: inline notifications persist until resolved or dismissed. Toasts without actions may auto-dismiss, but toasts with actions persist until dismissed. Actionable notifications persist until acted on. Banners are system-level. Callouts can't be dismissed. A notification panel collects notifications. — [Carbon Notifications](https://carbondesignsystem.com/patterns/notification-pattern/)
- Atlassian Flag: default flags are dismissible event messages. Bold flags (success, loading, warning, error) can't be dismissed until the issue is resolved or, for success, the message expires. — [Atlassian Flag](https://atlassian.design/components/flag/usage)

**Offline / flaky network**
- web.dev: "Tell the user both the application's state and the actions they can still take when they have a network failure". Example copy: "You seem to have a bad network connection. Not to worry! Messages will be sent when the network is restored." Tell users what state their data is in (e.g., synced). Avoid intrusive full-screen loading modals that block the UI on unstable networks; queue tasks and sync when connectivity returns. — [web.dev Offline UX design guidelines](https://web.dev/articles/offline-ux-design-guidelines)

**Conflicts (someone else changed this)**
- MDN: `If-Match` makes a `PUT` (or other unsafe method) conditional on the resource still matching the client's ETag. Otherwise the server returns `412 Precondition Failed`. — [MDN If-Match](https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/If-Match)
- SAP Fiori draft handling: an exclusive draft model with one owner locks the entity for other users until the lock expires. After that, others can discard the unsaved changes and edit. Subpage-level ("local flow") saving is recommended when several teams work on one object, or when the whole object shouldn't be locked. (via search summary) — [SAP Fiori Draft Handling](https://www.sap.com/design-system/fiori-design-web/v1-84/foundations/best-practices/global-patterns/object-handling/draft-handling)
- Product examples: a form system stamps records with a RowVersion and rejects stale saves with a "modified by another user" message while keeping the user's input in the form. Quick Base shows the specific conflicting fields and asks whether to overwrite. Rex and GitLab show presence ("someone else is viewing/editing") to prevent conflicts. (via search summary; vendor docs/issues) — [PMA formdev: Concurrent edits](https://formdev.pma.co.uk/docs/business-rules/cross-cutting/concurrent-edits/); [Rex update](https://rexsoftware.com/updates/see-whos-made-changes-while-youve-been-on-a-record-never-lose-your-edits-again); [GitLab issue 378650](https://gitlab.com/gitlab-org/gitlab/-/issues/378650)

**Unknown outcome / idempotent retry**
- Stripe: idempotency keys let you "safely repeat the request without risk of creating a second object or performing the update twice" after a connection error. API v1 saves the status code and body of the first request, and retries with the same key return that saved response, including 500 errors. API v2 retries complete failed or partially failed work where possible. Use V4 UUIDs (up to 255 chars) and don't put sensitive data in keys. — [Stripe Idempotent requests](https://docs.stripe.com/api/idempotent_requests)

### Inferences
Suggested state copy for Unit Toko (Indonesian first; these are proposals, not sourced copy):
- **Empty, first use (division PIC, no orders):** "Belum ada pesanan. Mulai dari katalog untuk mengajukan pesanan pertama divisi Anda." [Buka katalog]
- **Empty, filtered:** "Tidak ada pesanan yang cocok dengan filter ini." [Hapus semua filter]
- **Empty, nothing to do (queue cleared):** "Semua bukti bayar sudah ditinjau." (no CTA, per Carbon)
- **Permission denied:** "Anda tidak memiliki akses ke Laporan Akuntansi. Akses diberikan oleh Administrator Koperasi." [Ajukan akses / Hubungi admin]. Don't render controls the user can't use. Where an action exists but isn't allowed right now, explain why in place (e.g., "Hanya Kepala Unit atau delegasinya yang dapat menyetujui").
- **Partial failure (dashboard widget):** an inline message inside the failed card only ("Data stok tidak dapat dimuat. [Coba lagi]"), while other cards keep working (progressive loading).
- **Offline / degraded (Staf Toko on the floor):** banner "Koneksi terputus. Perubahan Anda belum terkirim dan akan dikirim ulang saat koneksi pulih." with a per-item "Belum terkirim" badge. Don't use blocking modals.
- **Conflict (412 / version mismatch):** keep the user's input, show "Pesanan ini diubah oleh Dewi (Kepala Toko) pukul 10.42 setelah Anda membukanya." with a field-level diff and options [Muat versi terbaru] / [Tinjau perbedaan]. Never auto-overwrite.
- **Unknown outcome (timeout or lost response after Submit/Approve):** don't show "Gagal". Show a neutral state: "Kami belum dapat memastikan apakah persetujuan Anda tercatat." with a primary [Periksa hasil], which re-sends the *original* request with the *same* idempotency key and so returns the stored outcome. Disable the original submit button while unresolved to prevent a new key. When resolved, show either "Persetujuan sudah tercatat pada 10.43 (ref. APV-…)" or "Belum tercatat — [Kirim lagi]" (same key).
- Never issue a new idempotency key on a user-initiated retry of the same intent. A new key only applies when the user changes the content (new intent).
- Toasts: auto-dismiss only for pure successes. Anything with an action ("Batalkan", "Lihat") or any error must persist (Carbon, Atlassian).

### Gaps
- No design system I fetched gives an explicit "unknown outcome / check result" pattern. The guidance above combines Stripe's API semantics, GOV.UK's "what happened to your answers" rule, and NN/g's preserve-input rule.
- I didn't fetch Atlassian's error/empty-state writing guidelines (the URL I tried returned 404).
- I found no primary design-system doc for conflict-resolution UI. The conflict evidence is product docs plus SAP via search summary.

## 6. Navigation and information architecture for many-role apps (role dashboards, sidebar vs top nav, command palette/search, breadcrumbs)

### Takeaway
Use a left vertical navigation with visible text labels (not icon-only) for apps with many sections, ordered by role relevance with less-used items lower. Give each role an operational home (time-sensitive work first). Use breadcrumbs only where the hierarchy is 3 or more levels deep, with the current page not a link. A command palette (Ctrl/Cmd+K) is a power-user accelerator that should expose every action. It supplements the visible navigation and doesn't replace it.

### Cited Findings
- NN/g (left-side vertical navigation): showing only icons and hiding text labels behind a click increases interaction cost (NOAA example, two clicks to navigate). In long menus, put less-important items at the bottom because of the page fold. — [NN/g Left-Side Vertical Navigation](https://www.nngroup.com/articles/vertical-nav/)
- NN/g breadcrumbs: the breadcrumb for the current page should not be a link and should look different from the linked ones. Breadcrumbs aren't needed for flat (1–2 level) or linear structures. Highlight the current section in the main navigation instead. — [NN/g Breadcrumbs: 11 Guidelines](https://www.nngroup.com/articles/breadcrumbs/)
- NN/g: operational dashboards serve time-sensitive tasks and present continuously updating data quickly. Analytical dashboards support analysis and decisions without the same time pressure. — [NN/g Dashboards](https://www.nngroup.com/articles/dashboards-preattentive/)
- NN/g accelerators: keep common keyboard shortcuts visible in the interface, styled differently from command labels (e.g., right-aligned in menus as in Intercom), so experts spot them and novices can ignore them. — [NN/g Accelerators](https://www.nngroup.com/articles/ui-accelerators/)
- Superhuman: make the command palette "omnipotent" (access to every action), so users default to it. Treat every action as "a UI element, a keyboard shortcut, and a command". Command palettes should be visually prominent. — [Superhuman: How to build a remarkable command palette](https://blog.superhuman.com/how-to-build-a-remarkable-command-palette/)
- Retool used one palette surface for both finding things and running actions, unlike VS Code's separate modes. Practitioner advice: open with Cmd/Ctrl+K, use fuzzy matching and category groups, key commands by stable IDs, and keep visible navigation primary while the product is small. (via search summary; practitioner sources) — [Retool: Designing the command palette](https://retool.com/blog/designing-the-command-palette); [UX Patterns for Developers: Command palette](https://skillselion.com/skills/thedaviddias/ux-patterns-for-developers/command-palette)
- Shopify index pattern: give breadcrumbs or a Back button so users don't depend on the browser button. (via search summary) — [Shopify App Home index pattern](https://shopify.dev/docs/api/app-home/patterns/templates/index)

### Inferences
- Use a left sidebar grouped by domain (Belanja/Pesanan · Pemenuhan · Pembayaran · Retur · Pengadaan · Persediaan · Laporan · Administrasi). Show only groups the user's grants allow, with visible labels, collapsible to icons with tooltips only on narrow screens. Put "Pekerjaan saya" with its badge count at the top for every role.
- Role homes: the division PIC home shows active orders by status, pending substitution consents and recent deliveries to confirm. Kepala Toko sees orders to accept, vendor POs and stock alerts. Staf Toko sees today's picking, dispatch and receiving tasks, with large touch targets. Billing sees proofs to review, unallocated suspense funds and overdue invoices. Unit head sees approvals pending with amount and age. Accounting gets an analytical dashboard with period filters. Each home is operational first (action items), with analytics below.
- Use a global search / command palette (Ctrl+K) with code+name lookup for orders (PO number), SKUs, divisions, invoices and users, plus verbs ("Setujui…", "Buat PO…"), filtered by permissions. Show shortcuts next to menu items.
- Breadcrumbs only for deep paths, e.g., Pembayaran › Bukti bayar › BP-2026-0091 › Alokasi. Flat list pages don't need them.
- With delegation, the header should always show the acting identity ("Bertindak sebagai: Kepala Unit (delegasi dari Budi)").

### Gaps
- I didn't retrieve primary Carbon UI Shell or Atlassian navigation guidance on sidebar vs top nav for enterprise apps.
- I found no peer-reviewed or NN/g research specifically on command palettes. Evidence is practitioner and vendor blogs.
- I found no specific research on role-based home dashboards for multi-role B2B tools.

## 7. Forms for operations (inline validation, keyboard efficiency, autosave drafts, number/currency inputs, quantity steppers, pickers)

### Takeaway
Validate on blur, not while typing. Show errors next to fields plus a linked error summary at the top on submit. Preserve all input. Use `type="text"` with `inputmode="numeric"` for whole numbers and IDs, and prefix/suffix labels for units and currency. Use steppers only for small-value adjustments. Autosave drafts with a visible saving indicator, and warn on navigation away with Save / Keep draft / Discard choices.

### Cited Findings
- NN/g: "Don't Validate Fields Before Input is Complete". Generally wait until the user has finished the field and moved on. — [NN/g 10 Guidelines for Reporting Errors in Forms](https://www.nngroup.com/articles/errors-forms-design-guidelines/)
- NN/g: keep error messages next to fields. They should be "explicit, human-readable, polite, precise, and give constructive advice". Don't use a validation summary as the only error indicator. — [NN/g Errors in Forms](https://www.nngroup.com/articles/errors-forms-design-guidelines/)
- GOV.UK error summary: "You must link the errors in the error summary to the answer they relate to". The summary title is "There is a problem", and each item links to the field (e.g., "Enter your full name"). — [GOV.UK Error summary](https://design-system.service.gov.uk/components/error-summary/)
- React Hook Form's `mode: 'onTouched'` validates first on blur, then on every change. That matches "validate after completion, then give live feedback while fixing." — [React Hook Form useForm](https://react-hook-form.com/docs/useform)
- GOV.UK text input: for whole numbers set `inputmode="numeric"` on a `type="text"` input to get the numeric keypad. Example markup includes `spellcheck="false"` and fixed-width classes. Prefixes and suffixes (e.g., "kg") show units, with the affix `aria-hidden="true"`. — [GOV.UK Text input](https://design-system.service.gov.uk/components/text-input/)
- Carbon Number input: increment/decrement controls "reduce input efforts for adjusting small values". Anatomy includes a required label, helper text (often for format), and error or warning text that replaces the helper text. — [Carbon Number input](https://carbondesignsystem.com/components/number-input/usage/)
- NN/g error messages: preserve user input and safeguard against likely mistakes (e.g., warn about a forgotten attachment before sending). — [NN/g Error-Message Guidelines](https://www.nngroup.com/articles/error-message-guidelines/)
- SAP Fiori draft handling: drafts save automatically in the background with a saving indicator. A draft can be validated for consistency and completeness at any time, returning a message list. On forward navigation with unsaved changes the app warns and offers Save (or Create), Keep draft, Discard draft, or Cancel. Simple-object create pages put "Create" and "Discard Draft" in the footer. (via search summary) — [SAP Fiori Draft Handling](https://www.sap.com/design-system/fiori-design-web/v1-84/foundations/best-practices/global-patterns/object-handling/draft-handling); [SAP Fiori Simple Objects](https://www.sap.com/design-system/fiori-design-web/v1-148/foundations/best-practices/global-patterns/object-handling/manage-simple-objects)
- NN/g accelerators: show keyboard shortcuts inline for frequent actions. — [NN/g Accelerators](https://www.nngroup.com/articles/ui-accelerators/)
- Baymard has 270+ B2B ecommerce UX guidelines from usability testing of 25+ B2B sites. Specific quick-order or bulk-entry guidance is behind a paywall and wasn't retrievable. (via search summary) — [Baymard B2B Ecommerce UX Research](https://baymard.com/research/business-to-business)

### Inferences
- **Currency (Rupiah):** use a text input with `inputmode="numeric"`, a fixed "Rp" prefix, live thousand separators using Indonesian formatting ("4.250.000"), parsing that tolerates pasted "Rp 4.250.000,00", and right-aligned display in tables. Don't use `type="number"`: scroll-wheel changes and loss of leading zeros are known issues, and GOV.UK avoids it for numbers.
- **Quantity:** use a stepper (−/+) for small cart and picking quantities, with direct typing allowed and unit as suffix ("pcs", "dus"). Validate against stock or ordered qty on blur, with a warning (not an error) when picking more than ordered. For bulk entry (procurement lines, stock count), use a keyboard-first grid: Tab/Enter moves to the next row, and the pick list is ordered by bin location.
- **Pickers (SKU, division, vendor, user):** use an async combobox matching both code and name ("BRG-00123 — Kertas A4 80g"). Show code, name and a secondary attribute (stock, division unit) in results, plus recent selections, and keep full keyboard support (arrow keys, Enter, Esc).
- **Drafts:** autosave carts, PO drafts, stock-count sessions and return requests, with a "Tersimpan otomatis 10.42" indicator. Use the Fiori leave-page choices (Simpan / Simpan sebagai draf / Buang / Batal). Show existing drafts on the role home.
- **Submission:** the submit button shows a pending state and is disabled while the request is in flight, reusing the same idempotency key on retry (see section 5). Server-side errors map back onto fields plus the linked error summary.
- **Reason fields** (reject, decline substitution, payment rejection): offer predefined reason chips plus an optional free-text note, so reasons are consistent enough to report on.

### Gaps
- The Carbon Combo box usage page returned 404. I have no primary design-system guidance on async code+name pickers. The GOV.UK accessible-autocomplete or Atlassian user-picker docs would be good next sources.
- I found no primary research on currency-input formatting (live vs on-blur thousand separators) for Indonesian Rupiah.
- Baymard's B2B quick-order, reorder-list and bulk-entry guidelines are paywalled. I found no free primary source for B2B bulk ordering UX.
- Material 3 guidance was not reviewed in this pass.
