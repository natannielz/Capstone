# Approver, access/organisation administrator and staff "task inbox" patterns (procurement and business software, 2025–2026)

Scope note: back-office screens only. Sources were fetched where possible. Items marked **[search extract]** come from a search-engine extract of the cited page because the page body could not be fetched (JS-rendered, 403 or redirect). Treat those as lower confidence.

---

## 1. How do procurement and expense tools present approval requests (inbox, detail, approve/reject, bulk, mobile, email), and how do they notify, remind and escalate?

### Takeaway
Every reference product has two ways in: a list or queue ("Approve Orders", "To Do", "Requiring My Approval", "Needs My Approval", "My Task → Pending Task") and a detail page showing the full request (line items, shipping address, payment method, coding fields, approver chain), with Approve/Reject plus a comment. The mature patterns are: reject a single line while approving the rest (Amazon), a required reason on reject (ServiceNow, Salesforce add-ons), bulk select with one shared comment (Talenta, Salesforce, RapidApproval), and a hard expiry on a pending request with a reminder before it (Amazon: 7 days, reminder within 3). Approving by email is common, but forwarded links are a documented hole. The fix is to make the email a deep link that requires a fresh sign-in. Banking-grade tools (BNIdirect, ServiceNow e-signature) require a token or re-authentication on each approval.

### Cited Findings
**Amazon Business (Approvals / Business Order Information)**
- Approvers are notified "by email, on their dashboard, and when they log in". On the order details page approvers check the shipping address, payment method and all requested items, and some organisations also show a PO field. — [Amazon Business: Approving Requests](https://business.amazon.com/en/learn/approving-requests)
- A cart awaiting approval keeps prices and availability locked for 7 days, including weekends. After that the cart cancels and the buyer must resubmit, possibly at changed prices or quantities. — [Amazon Business: Approving Requests](https://business.amazon.com/en/learn/approving-requests)
- Approvers can reject single items while other items move forward. They must reject the whole order if the shipping address or payment method is wrong. Adding a comment on rejection is encouraged so buyers can fix and resubmit, and buyers get an email with a link to rebuild the rejected cart. — [Amazon Business: Approving Requests](https://business.amazon.com/en/learn/approving-requests)
- Email notification goes to the designated approvers on submission. "Reminders are sent within three days if the order hasn't been approved or rejected." Approvers can approve from the Amazon Business Mobile App. The blog headline is "Delegate approvers for policies, approve purchases in bulk, and approve on-the-go". — [Amazon Business blog, Sept 2023](https://business.amazon.com/en/discover-more/blog/reduce-back-forth-approval-workflows)
- Release of 25 April 2025: "Approvers can easily search, sort, and filter purchase requests". Approvers can search, sort and filter both "pending approval" and "approval history" to group orders, then batch approve or reject. — [Amazon Business release note](https://business.amazon.com/en/release-notes/search-sort-filter-purchase-requests) **[search extract for the body]**
- Up to 6 approval levels. Each level can have more than one approver, and only one approval is needed per level. — [Univ. of Oregon Amazon setup guide](https://pcs.uoregon.edu/sites/default/files/amazonhandouts/Setting%20Up%20Approvals%20and%20Workflow%20Amazon.pdf) **[search extract]**
- Business Prime Medium and Enterprise customers get an extended 14-day approval window. — [Amazon Business buying policies release note](https://business.amazon.com/en/blog/release-notes/buying-policies-approvals) **[search extract]**
- Business Order Information (BOI) checkout fields include "PO number, GL code, Cost center, Location, Department, and Comments for approver". For orders that need approval, the checkout button reads "Submit order for approval" instead of "Place your order". — [Amazon help: Check out with Amazon Business](https://www.amazon.com/gp/help/customer/display.html?nodeId=201633090)
- BOI admin set-up:
  - Fields are enabled per field, with at most 19 fields.
  - Each field is either "Open text field" or "List of defined options", with up to 500 options of up to 30 characters each.
  - A "Required" checkbox makes a field mandatory, and a buyer guidance note of up to 200 characters can be added.
  - Group settings override organisation settings.
  
  — [Amazon help: Set up business order information fields](https://www.amazon.com/gp/help/customer/display.html?nodeId=Tde0UyCJTVS0egg8X3)
- Approval workflows can be routed on BOI values such as PO, GL code, cost center, project code, location and department, "to appropriate approvers or approver workflows with different levels". The admin path is Business Settings → Buying policies and approvals → Add policy → Order policies → "Business order information approval". — [Amazon Business: Business Order Information](https://business.amazon.com/en/solutions/spend-insights/business-order-information)

**Coupa**
- Ways to approve:
  - "To Do's" on the homepage: click the subject to open the detail, add "Approval Comments", then Reject or Approve. All approvers show in an "Approvers" section.
  - "Inbox", with the view switched to "To Do".
  - The Activity tabs with the "Requiring My Approval" view.
  - Email with View Req / Approve / Reject actions.
  
  A rejected requisition returns to "Draft" for the requester to fix and resubmit. — [GitLab Handbook: Coupa End Users Guide](https://handbook.gitlab.com/handbook/business-technology/enterprise-applications/guides/coupa-guide/)
- Approvers can add another approver "after the current approver, end of the chain, or only as a watcher". "A watcher cannot approve or reject the cart but will receive notifications". "The greater the amount of the requisition, the more approvers will be needed", and applicable approvers show beneath the cart line. — [GitLab Handbook: Coupa guide](https://handbook.gitlab.com/handbook/business-technology/enterprise-applications/guides/coupa-guide/)
- Coupa's advice for shorter chains:
  - Add finance and accounting as watchers rather than approvers.
  - "Set up automatic routing to alternate approvers in the event an approver is out of the office or fails to respond within a specified time frame."
  - "Set budgets and make them visible."
  - Default "to the lowest possible approval level, including self-approvals".
  
  — [Coupa blog, July 2024](https://www.coupa.com/blog/how-speed-requisition-order-time-shorter-approval-chains/)

**SAP (Fiori My Inbox)**
- The My Inbox app lists items with "Accept" and "Reject" buttons. Substitutes are managed from the user menu → "Manage My Substitutes" (see Q3). — [SAP Fiori whitepaper: Managing Substitutions in My Inbox 2.0](https://help.sap.com/doc/fioritech/1.0/en-US/attachments/3362178363/Managing_Substitutions_in_My_Inbox_2.0.pdf)

**Talenta (Mekari HRIS, Indonesia)**
- Approver inbox: Inbox → Delegation (and Inbox → Needs My Approval).
  - Status filter: All / Pending / Approved / Rejected.
  - Sort: Latest / Oldest.
  - Each item opens the full request with a comments box and "Accept" / "Reject".
  - Bulk: tick the header "Checklist box" to select all, then "Approve Request" or "Reject Request".
  
  — [Talenta: How to Manage Delegation on the Inbox Menu](https://help-center.talenta.co/hc/en-us/articles/9170113068185-How-to-Manage-Delegation-on-the-Inbox-Menu)
- Approval setup can auto-approve or auto-reject on a schedule ("Auto approval type": Auto approve / Auto reject). The trigger is the payroll cut-off, the attendance cut-off or a custom date, with "Exact date / Before date / After date". — [Talenta: How to Set or Change Approval](https://help-center.talenta.co/hc/en-us/articles/11371231766425-How-to-Set-or-Change-Approval)

**Mekari Expense (Indonesia)**
- Routing rules can be set by transaction amount, by type (reimbursement, travel request, purchase invoice, vendor payment), by department and by branch location.
  - "Custom Policy": non-compliant claims are flagged or blocked before they reach an approver, which keeps queues focused.
  - "Fraud AI Checker" flags duplicates and unusual amounts to give approvers context.
  - "Audit Trail": every submission, approval, revision request and clearance is logged with timestamp and approver identity.
  
  — [Mekari Expense blog: multi-layer approvals](https://expense.mekari.com/en/blog/how-to-set-up-multi-layer-expense-approvals)

**Mbiz (Indonesian B2B marketplace / e-procurement)**
- Mbizmarket's e-procurement suite includes online/digital approval, e-tendering, online invoicing, budget control and inventory tracking. Transactions "are recorded and can be audited". — [Gizmologi: Mbizmarket e-procurement](https://gizmologi.id/news/mbizmarket-e-procurement/) **[search extract]**

**Generic work-queue tools**
- ServiceNow Service Portal "Approvals" widget:
  - Reads the sysapproval_approver table and shows Approve/Reject buttons.
  - Instance option "Comments mandatory for rejection" (off by default) puts rejection comments on the approval record.
  - "Max number of elements shown in the list" defaults to 10.
  - E-signature can be enabled "to require reauthentication".
  
  — [ServiceNow docs: Approvals widget](https://github.com/ServiceNow/ServiceNowDocs/blob/australia/markdown/platform-user-interface/service-portal/approvals-widget.md)
- A ServiceNow practitioner notes that rejecting from the Core UI form already forces Comments, but the list right-click "Reject" action rejects without a reason. They recommend disabling it. Employee Service Center and Now Mobile already require a reason. — [David McDonald: Better SN Approvals #5, May 2025](https://davidmac.pro/posts/2025-05-16-approval-reject-reason/)
- Salesforce:
  - Approvers respond by approving, rejecting or reassigning. Comments are capped at 4,000 characters.
  - Responses can come from the Approval History related list, Chatter, in-app notification, or the "Items to Approve" Home component. In Classic, that component allows responding to multiple requests at once.
  - Email approval response needs the "API Enabled" permission, and "Delegated approvers can also respond to approval requests by email".
  
  — [Salesforce Help: Respond to an Approval Request](https://help.salesforce.com/s/articleView?id=platform.approvals_processing_approval_requests_parent.htm&language=en_US&type=5)
- RapidApproval (third-party Salesforce add-on):
  - The home component shows "Approval Type", count "In Queue" and "Oldest Request". A type shows in red when requests have waited beyond its configured days.
  - The queue is grouped by request type, with columns Checkbox / Action / configurable Column1 & Column2 / Submitted By / Submitted Date.
  - Actions are staged (green tick for approve, red X for reject) and then "Commit Actions". "Reset All" clears the staged actions.
  - Reject and Reassign require comments. With bulk selection the comment is entered once and applied to all.
  
  — [RapidApproval user guide (AppExchange)](https://appexchange.salesforce.com/partners/servlet/servlet.FileDownload?file=00P3A00000YSbOKUA1)

**Banking maker/checker (BNI, directly relevant to BNI branch users)**
- BNIdirect cash has three user types: Maker (inputs transactions), Approver (checks and validates the Maker's transactions) and Releaser (forwards approved transactions to the bank). Each has their own Company ID, User ID and password. — [BNI: BNIdirect](https://www.bni.co.id/en-us/home/bnidirect)
- Transaction authority is split into Maker, Approver and Releaser, together with "a transaction authority Matrix and transaction limits". — [BNI: Digital Disbursement and Payment Management](https://www.bni.co.id/en-us/corporate/solusi-wholesale/digital-disbursement-dan-payment-management)
- Approver and Releaser each log in and process the transaction "pada menu My Task → sub menu Pending Task dengan menggunakan token". — [AHU Online payment guide](https://panduan.ahu.go.id/doku.php?id=cara_pembayaran_simpadhu_pnbp)

**Approve-by-email risks**
- In a Lawson/Infor forum thread, an approval email forwarded to a non-approver let the recipient approve, effectively impersonating the original approver. The audit trail recorded a generic system account. Mitigations discussed: a newer version's flag requiring login when approving from email, and an in-email button that forces login. — [LawsonGuru forum](https://lawsonguru.com/forums/integration/ipa/process-flow-10-approval-from-forwarded-email) **[search extract]**
- Sprout HR: if the approver is logged out when clicking an email approval link, "the system will automatically redirect you to the main login page… to ensure that only an Authorized Person will be able to approve". The vendor frames this as protection when an approval email is "inadvertently or intentionally forwarded". — [Sprout: Email Approvals and Session Authentication](https://guide.sprout.ph/system-behavior-email-approvals-and-session-authentication)
- A feature request for secure email approvals lists the threats as forwarded links, token reuse, link scanners and concurrent actions. It also asks that audit history record the verified approver identity and decision. — [Hive feature request](https://feedback.hive.com/feature-requests/p/support-secure-approve-and-reject-actions-from-internal-approval-emails) **[search extract]**

### Inferences
- **"Persetujuan belanja" list columns**:
  - Cabang / divisi
  - Pemesan (requester)
  - No. pesanan
  - Total
  - Sisa anggaran setelah pesanan (budget remaining)
  - Aturan yang berlaku (the policy that triggered approval, e.g. "Melebihi batas Rp 5 jt per pesanan" or "Kategori: Elektronik")
  - Diajukan (age)
  - Kedaluwarsa dalam (time left before auto-cancel)
  
  Default sort should be oldest or most urgent first. This mirrors Amazon's sortable and filterable pending list and history, the Shopee "Due in 1 day" priority filter (Q6), and RapidApproval's "Oldest Request" column.
- **Detail page**:
  - Line items with qty × price, the delivery address and the payment method/term.
  - Coding fields (Amazon BOI–style: PO, cost center, "Catatan untuk penyetuju").
  - The rule that fired and the budget before and after.
  - The approver chain with each step's state.
  - History.
  
  Actions are "Setujui" and "Tolak" (reason required, as ServiceNow recommends), plus optionally "Tolak sebagian" per line (Amazon), which is not allowed when the address or payment is wrong.
- **Step-up MFA**: re-verify per decision, or once per bulk batch with the batch count and total displayed. This follows BNIdirect's token on Pending Task and ServiceNow's e-signature, and BNI branch staff already know the "My Task → Pending Task + token" mental model. Show the requester's name prominently and the fact that "Anda bukan pemesan" to make the four-eyes rule visible.
- **Email/WhatsApp notifications should deep-link only**: no one-click approve. The link should land on the sign-in plus step-up page, so a forwarded message is harmless. This combines the Sprout pattern with the Lawson failure mode.
- **Expiry and reminders**: Amazon holds a cart 7 days with a reminder within 3. Unit Toko could hold the price for N working days, remind at about 50% of the window, then escalate or auto-cancel, and show the countdown in both the list and the detail.
- **Bulk approve**: use RapidApproval/Talenta-style checkbox selection with a single comment, but restrict it to requests within the approver's scope. Consider excluding high-value requests from bulk.

### Gaps
- I could not fetch Amazon's approver list view itself, so the exact list column names are unverified. The same goes for the Business Prime 14-day window, which is a search extract.
- No public SAP Concur approver list-column documentation was retrieved, and SAP Ariba approval UI was not covered.
- Mekari Expense approver-screen labels (Bahasa) were not found. Its public pages are marketing.
- Mbiz's actual approval flow (levels, thresholds, UI) is undocumented publicly. Only feature claims were found.
- No authoritative security standard on email approvals was found. The evidence is vendor and forum material.

---

## 2. How is spending policy configured (thresholds, per-category rules, multi-step chains, "requester cannot approve own request", budget display)?

### Takeaway
Two configuration models dominate:
- **Approval limits on people plus a management hierarchy** (Coupa). The chain climbs until someone's limit covers the amount.
- **Policy-triggered chains** (Amazon, Coupa approval chains, Mekari, Talenta). Conditions on amount, category, branch or department, or a coding field, add specific approvers in a priority order.

Self-approval is an explicit, separately configurable setting (Coupa "Allow self-approvals", "Auto Approve Self", "Skip Approvers prior to self"). Rules are ordered from most specific to most general (Talenta), and there is a fallback "ultimate approver" when no one qualifies (Coupa).

### Cited Findings
- Amazon Business policy types:
  - Order policies restrict all orders, or orders over a set spending limit ("choose 'all orders' if you want to review every purchase").
  - Category policies set spending limits for, or block, UNSPSC categories, with approval workflows for orders exceeding a category threshold (added Dec 2020).
  - Item and seller policies mark items as preferred, restricted (warn or require approval) or blocked.
  
  — [Amazon Business: category spend release note](https://business.amazon.com/en/blog/release-notes/constrolspend-productcategory); [Amazon Business: Buying Policies and Approvals](https://business.amazon.com/en/find-solutions/buying-policies) **[search extract for body text]**
- Amazon spend limits are framed as "steering buyers to stay within identified limits, or seek approval for orders exceeding set spend limits". — [Amazon Business: Configure Spend Limits (Aug 2025)](https://business.amazon.com/en/learn/spending-limit)
- Coupa builds approvals from two sources:
  - The **management hierarchy**, built from each user's "Next approver", "self approval limit" and approval limit. "If the original requester has a self approval limit less than the requisition… Coupa adds their next approver… until someone's limit is high enough". If no one qualifies, "Coupa add[s] your system's ultimate approver to the end of the chain".
  - **Custom approval chains**, each with conditions, a priority and approvers.
  
  — [Coupa Approvals guide (PDF)](https://get.coupa.com/rs/950-OLU-185/images/Approvals.pdf)
- Coupa chain mechanics:
  - Priority runs from 1 (first) to 100 (last). The management hierarchy sits at 50, so 49 or lower inserts before it and 51 or higher after.
  - All conditions must be met. The first condition line is the total amount; the others use the operators Equals / Not equal to / Starts with / Contains / Does not contain / Is blank / Is not blank, on fields such as account, supplier, commodity or a custom field.
  - Each specific approver has a per-chain maximum amount, and Coupa adds approvers until one covers the total. Alternatively, a "minimum position" in the hierarchy can be set.
  - "Watchers" get visibility without being asked to approve.
  
  — [Coupa Approvals guide (PDF)](https://get.coupa.com/rs/950-OLU-185/images/Approvals.pdf)
- Coupa approval settings include "Build approval path based on management hierarchy", "…based on approval chains", "Allow self-approvals" (requires self-approval limits on each user) and "Notify manager on self approval" (requisitions only). If a user lacks a Next approver, Coupa treats the chain as broken and "Calls your ultimate approver". — [Coupa Approvals guide (PDF)](https://get.coupa.com/rs/950-OLU-185/images/Approvals.pdf)
- Coupa approval-chain import fields include:
  - Approval Chain Type, with blocking and warning variants (e.g. requisition-submission-blocking, invoice-submission-warning).
  - Skip Approvers ("prior to self if on approval chain"), Skip Management Hierarchy, Skip Further Approvers and Auto Approve Self.
  - "Dedupe OBO User and Auto Approve", Parallel, and Minimum/Maximum Total Amount with currency.
  - Up to 10 conditions and 10 approvers ("user_login;amount;currency_code") plus 5 watchers.
  - Approver Params such as "escalation:User:<login>" or "escalation:ApprovalGroup:<name>".
  - A "Description" for custom notification text explaining the condition.
  
  — [Coupa docs: Approval Chain Import](https://docs.coupa.com/en/developer-documentation/coupa-core-flat-files-csv/flat-file-csv-import/approval-chain-import)
- Coupa user attributes include Requisition Approval Limit Amount/Currency, Invoice Approval Limit, Requisition Self-Approval Limit and Approval Groups. — [Ping Identity Coupa connector attribute index](https://prod-docs.pingidentity.com/integrations/coupa/setup/pf_coupa_connector_attribute_index.html)
- Mekari Expense describes three rule types:
  - **Amount-based**: e.g. under a threshold needs one approval, above it two, above a higher ceiling finance sign-off.
  - **Category-based**: travel to the travel manager or HR, vendor invoices to AP, capex to the CFO.
  - **Branch or department**: "require cost center approval before expenses can be charged to a specific branch budget".
  
  A second level is "triggered automatically when the claim exceeds a defined threshold". — [Mekari Expense blog](https://expense.mekari.com/en/blog/how-to-set-up-multi-layer-expense-approvals)
- Talenta approval lines (Settings → ESS → Approval):
  - The "Setting" column picks the subject: all employees, job, organization, job level, employee ID or branch.
  - Rows are ordered with Up/Down and evaluated top to bottom until one matches, so exceptions go at the top, "sorted from the most specific to the most general (All Employee)".
  - "Auto Ascend" forwards to the upper layer if the approver position is empty.
  - Multiple layers are added via "Add more". An approver is defined by Upper Job Position, Employee ID or Consultant.
  
  — [Talenta: How to Set or Change Approval](https://help-center.talenta.co/hc/en-us/articles/11371231766425-How-to-Set-or-Change-Approval)

### Inferences
- **Branch spending rule editor**: model each rule as a row of [scope: buying org / wilayah / cabang / divisi] × [condition: order total > X, category in {…}, monthly cabang budget remaining < Y] → [approver: role at the same cabang, or a named person] + [priority]. Show rules in evaluation order with "paling spesifik di atas" (Talenta). Show a fallback approver for when no one qualifies (Coupa ultimate approver).
- **"Pemesan tidak boleh menyetujui pesanannya sendiri"** should be a non-negotiable system invariant for Unit Toko, not a toggle. Coupa's "Allow self-approvals" setting shows the industry treats this as configurable, but the brief requires a different person. If the requester is also the cabang's only eligible approver, route to the next level (the Coupa "Skip Approvers prior to self" equivalent) and tell the requester why.
- **Blocking vs warning rules**: Coupa's submission-blocking and submission-warning chain types map to "Pesanan tidak dapat diajukan" vs "Perlu persetujuan". Each rule should carry human-readable reason text (Coupa "Description") that is shown to both buyer and approver.
- **Budget display**: show "Anggaran bulan ini: terpakai / sisa / setelah pesanan ini" on checkout and on the approval detail (Coupa: "Set budgets and make them visible").

### Gaps
- No primary Amazon admin screen for policy creation (fields and labels) was fetched. Amazon policy descriptions come partly from search extracts.
- SAP Concur, SAP Ariba and Mbiz policy-configuration UIs were not retrieved.
- No source showed a "budget remaining" column on an approver list. This is a recommendation, not an observed pattern.

---

## 3. How does delegation work while an approver is out of office (date range, scope, audit) in Concur, SAP, Talenta, Coupa, Google Workspace and Microsoft Entra?

### Takeaway
Delegation is a dated rule, created by the approver or an admin, with five parts:
- a from-person and a to-person,
- a start and end date,
- a scope (which modules or task types),
- a reason,
- constraints on who can be a delegate (same or higher level, same group or branch).

Two execution models exist:
- **Parallel**: both the original approver and the delegate receive the item, and the first to act wins (Talenta).
- **Substitute/forward**: the delegate sees the items while the rule is active (SAP planned or unplanned substitution, Concur "Can Approve Temporary").

Identity platforms (Entra PIM) express the same idea as time-bound role assignments with expiry, justification and notifications.

### Cited Findings
- **SAP Concur**: the "Can Approve Temporary" delegate option permits approving on the user's behalf "for a temporary period of time", and a start and end date must be entered. A separate "Receives Approval Emails" option copies notifications to the delegate, while the original approver still receives notifications. — [Pennsylvania SAP Concur user guide](https://www.budget.pa.gov/Services/ForCommonwealthTravelers/SAPConcur/Documents/SAP%20Concur%20User%20Guide%20-%20Assigning%20Expense%20Delegates%20and%20Travel%20Arrangers.pdf) **[search extract; direct fetch redirected]**; same steps in [Gloucestershire guide](https://www.gloucestershire.gov.uk/media/fhxpwrmb/giving-delegate-access-to-other-users.pdf) **[search extract]**
- **Concur Community**: approver functions can be delegated "permanently or for a period", and absence handling can also be configured in workflows or approval routing. — [Concur Community: Adding a Delegate](https://community.concur.com/t5/Concur-Expense/Adding-a-Delegate/td-p/85917) **[search extract]**
- **SAP Fiori My Inbox**:
  - User menu → "Manage My Substitutes" has "Planned" and "Unplanned" tabs.
  - Planned: "Add New Substitute", search for the user, choose a substitution profile (which task classes are covered) and a substitution period, then Save. A missing end date produces an error.
  - Unplanned: no period. The substitute must actively take over via "Substitute for" and switch the rule on (the switch turns green).
  
  — [SAP whitepaper: Managing Substitutions in My Inbox 2.0](https://help.sap.com/doc/fioritech/1.0/en-US/attachments/3362178363/Managing_Substitutions_in_My_Inbox_2.0.pdf); [SAP KBA 3020379](https://userapps.support.sap.com/sap/support/knowledge/en/3020379)
- **Coupa**: Account Name → Settings → "Delegate" subtab → Create, choosing the delegate type (Receiving, Delegate) "for a specified timeframe and give a reason". "Assigned delegates must be of an equivalent or greater management level", and for approval-group approvals the delegate must be a member of that group. — [GitLab Handbook: Coupa guide](https://handbook.gitlab.com/handbook/business-technology/enterprise-applications/guides/coupa-guide/)
- **Talenta**:
  - Set-up: Settings → ESS → "Delegation" → "Add delegation". Choose "From employee" and "To employee", the "Delegation date" (start/end), and the modules or authority to delegate (individually or "Select all").
  - Delegation restrictions can limit the "Delegate to" list by Branch, Organization and similar categories, and notification reminders can be set afterwards.
  - It "functions as a parallel layer approval": the submission goes to both the main approver and the delegate, and only the first responder's approve or reject counts.
  
  — [Talenta: How to Create and Manage Delegation Settings (updated 2026)](https://help-center.talenta.co/hc/en-us/articles/11388182960793-How-to-Create-and-Manage-Delegation-Settings)
- **Amazon Business**: approvers can delegate a user to approve on their behalf. Marketing headline: "Delegate approvers for policies". — [Amazon Business blog](https://business.amazon.com/en/discover-more/blog/reduce-back-forth-approval-workflows)
- **Microsoft Entra PIM**:
  - Role settings ("PIM policies") are defined per role and cover MFA and approval requirements for activation, maximum assignment duration and notifications.
  - Eligible and active assignments can each be "permanent" or "Expire … after", which requires a start and end date.
  - "Require justification on active assignment" and "Require ticket information on activation" (informational) are available.
  - Notifications can go to default and/or extra recipients, with a "Critical emails only" option.
  - Users can self-request extend or renew.
  
  — [Microsoft Learn: Configure Entra role settings in PIM](https://learn.microsoft.com/en-us/entra/id-governance/privileged-identity-management/pim-how-to-change-default-settings)
- **Entra entitlement management**: access packages with multi-stage approval, "time-limited assignments and recurring access reviews". Non-administrator "access package managers" can be delegated to define who can request, who must approve and "when access expires". — [Microsoft Learn: What is entitlement management?](https://learn.microsoft.com/en-us/entra/id-governance/entitlement-management-overview)

### Inferences
- **"Delegasi sementara" form**:
  - Dari (penyetuju), Kepada (delegasi), Mulai/Selesai (end date required, no open-ended delegation, following SAP's error on a missing end date).
  - Cakupan: which approval types, e.g. Persetujuan belanja only, or also price/stock checks for koperasi staff.
  - Alasan (Coupa).
  - Eligibility filter: the delegate must hold an approver-capable role in the same cabang or scope (Talenta's restriction by Branch, Coupa's "equivalent or greater level").
  - The delegate must never be the requester of the item being approved. The four-eyes rule must re-check at decision time.
- **Parallel model**: prefer Talenta-style parallel delivery (both can act, first decision wins) so the original approver isn't locked out if they return early. Show "Disetujui oleh X atas nama Y (delegasi s/d 30 Okt)" in the request history and audit log.
- **Expiry and notice**: show active delegations as a banner in both users' "Pekerjaan saya" and let them be revoked early. Notify both parties at start and end, as Concur keeps the original approver notified. Treat creating a delegation as a sensitive action needing step-up MFA, the same as approving.

### Gaps
- Google Workspace has no approval-delegation feature comparable to procurement delegation. Its Gmail mailbox delegation was not researched, since it is a mailbox, not an approval scope.
- I could not fetch the Concur PDFs directly. Concur's delegate-screen field names are from search extracts.
- No source found on how a delegation itself is approved (e.g. whether an admin must approve it). Talenta's Inbox → Delegation implies delegation submissions can require approval, but details are thin.

---

## 4. How do admin tools handle invitations and role assignment with scope (role templates, scope pickers, permission previews, least privilege, expiring access, access reviews)?

### Takeaway
The consistent pattern is to assign predefined roles (templates) rather than raw permissions. Roles are cumulative, split by "view only" vs "view & edit", and can be scoped to an organisational unit or admin unit. Invitations go by email with an activation deadline and a visible status. User management itself is reserved to the owner or super admin. Mature platforms add time-bound or expiring assignments (Entra PIM, Tokopedia's old paid "periode akses"), periodic access reviews with recommendations (Entra), and a second-admin approval for sensitive changes (Google Workspace multi-party approval). Indonesian marketplaces (Shopee Sub Akun, TikTok Shop by Tokopedia) ship 6–9 default roles plus custom roles and an activity log.

### Cited Findings
**Google Workspace Admin console**
- "The easiest way to give administrator privileges… is to assign prebuilt administrator roles. Each role grants one or more privileges that together allow you to perform a common business function." Custom roles exist for specific privileges. — [Google: Prebuilt administrator roles](https://support.google.com/a/answer/2405986?hl=en)
- Account → Admin roles:
  - Hover a role → "View privileges" or "View admins" (a permission preview).
  - "Assign admin" toggles between "Admins" and "Privileges". "Assign members" assigns up to 20 users or groups at once, by email.
  - Optionally, "To restrict the admin's role to a specific organizational unit, next to All organizational units, click Edit".
  - Groups Reader/Editor roles can be limited to certain group types.
  
  — [Google: Assign specific admin roles](https://support.google.com/a/answer/9807615?hl=en)
- Multi-party approval:
  - Sensitive Admin console changes (e.g. 2-Step Verification settings) can require a second admin's approval. For super-admin-only actions, "a second super administrator must review".
  - Requests are under Security → Authentication → "Multi-party approval requests", with "Approve request" / "Deny request", and "Requests submitted" → "Cancel request" for one's own.
  - Pending requests "remain active until they are approved, denied, canceled, or expire".
  
  — [Google: Multi-party approval for sensitive actions](https://support.google.com/a/answer/13790448?hl=en)

**Microsoft Entra**
- Roles can be assigned with an administrative-unit scope to users, role-assignable groups and service principals. — [Microsoft Learn: Assign Microsoft Entra roles](https://learn.microsoft.com/en-us/entra/identity/role-based-access-control/admin-units-assign-roles)
- PIM: eligible vs active assignments, with expiry, justification, MFA and approval on activation (see Q3). — [Microsoft Learn: PIM role settings](https://learn.microsoft.com/en-us/entra/id-governance/privileged-identity-management/pim-how-to-change-default-settings)
- Access reviews are recommended when there are "Too many users in privileged roles", including invited guests not removed after a task. — [Microsoft Learn: What are access reviews?](https://learn.microsoft.com/en-us/entra/id-governance/access-reviews-overview)
- Reviewer UX:
  - Select users → "Approve" / "Deny" / "Don't know" ("Don't know" keeps access and is recorded in audit logs). There is a "Reason" box, which may be required, and responses can be changed until the review ends.
  - Denied users are removed at review end, not immediately.
  - "Accept recommendations" applies to users with "No sign-in within 30 days" and to "Peer outlier".
  - In multi-stage reviews, later reviewers see prior-stage decisions.
  
  — [Microsoft Learn: Review access in access reviews](https://learn.microsoft.com/en-us/entra/id-governance/perform-access-review)

**Shopify**
- "Peran mewakili pekerjaan anggota staf" (a role represents a staff member's job). Predefined roles exist, e.g. "Merchandiser" gives access to Produk, Katalog and Konten. Roles are managed at Pengaturan → Pengguna → Peran. Multiple roles per user give cumulative permissions, and each role has a category and a set of permissions. — [Shopify Help (ID): Peran](https://help.shopify.com/id/manual/your-account/users/roles)

**Shopee Seller Centre "Sistem Sub Akun"**
- Menus:
  - "Toko Saya": shops plus the roles and access per shop.
  - "Tim Saya" → "Anggota" (members overview, add member) and "Peran" (view roles, add role name, description and access details).
  - "Pembagian Chat".
  - "Aktivitas": activity of member accounts across the Sub Akun system and Seller Centre.
  - "Akun Saya".
  
  Default roles: "Admin, Merchandiser, Penyelesaian Pesanan, Keuangan, Customer Service, dan KOL Livestreamer". Some functions are PC-only, including assigning tasks and changing member access or email/password. — [Shopee Edu ID: Memulai Sistem Sub Akun (2026)](https://seller.shopee.co.id/edu/article/8572)
- The stated purpose is to prevent misuse such as changing prices, changing shipping, "ekspor data yang bersifat rahasia" or setting discounts without the owner's knowledge, without sharing the main account's credentials. Custom roles can be added beyond the 6 defaults. — [Shopee Edu ID: Optimasi Pembagian Tugas dengan Sistem Sub Akun](https://seller.shopee.co.id/edu/article/13907)

**TikTok Shop by Tokopedia (the current Tokopedia seller centre)**
- Path: Seller Center → Akun Saya → Pengaturan Akun → "Manajemen Pengguna".
  - The list has a filter bar by role, status and email.
  - "Buat Baru" → choose role → enter email → choose language → send.
  - An activation email is sent, and the user "harus mengaktifkan akun mereka dalam waktu 120 jam". The list shows activation status.
  - Roles can be edited after activation.
  
  — [Tokopedia Seller University: Manajemen Akses di Seller Center (24/03/2026)](https://seller-id.tokopedia.com/university/essay?knowledge_id=6837736040679169&lang=id-ID)
- Permissions follow Seller Center menus, split into "Hanya melihat" and "Melihat & Mengedit", and some can't be split. There are nine default roles. FAQ answers:
  - "Dapatkah saya secara langsung menetapkan izin ke sub-akun? Tidak, Anda hanya dapat menetapkan peran" (permissions come only via roles).
  - Only the store-owner account can assign or customise roles.
  - There are no location-scoped sub-account roles. Sub-accounts use a warehouse-location filter instead.
  
  — [Tokopedia Seller University: Manajemen Akses](https://seller-id.tokopedia.com/university/essay?knowledge_id=6837736040679169&lang=id-ID)
- Legacy Tokopedia "Admin Toko" access types and their mapping:
  - Kelola Toko → Administrator Utama
  - Atur Produk; Ubah Stok Produk → Spesialis Manajemen Produk
  - Balas Pesan Pembeli → Agen Customer Service
  - Kelola Pesanan Toko → Spesialis Pemenuhan Pesanan
  - Pantau Statistik → Administrator Utama
  - Pengaturan Iklan & Promosi → Spesialis Pemasaran / Manajer Periklanan
  - Kelola Keuangan → Spesialis Keuangan
  - Location-scoped "Admin Lokasi" roles: Kelola Pesanan Lokasi, Atur Jumlah Stok
  
  — [Tokopedia Seller University: Manajemen Akses](https://seller-id.tokopedia.com/university/essay?knowledge_id=6837736040679169&lang=id-ID)
- A Seller University course shows a role × page matrix (Admin / Customer Service / Warehouse / Finance, each page marked "View & Edit", "View Only", ❌ or "Owner Only"). It says access can be customised per employee via "Edit Access". — [Tokopedia Seller University course: Manajemen Pengguna (Oct 2025)](https://seller-id.tokopedia.com/university/course?content_id=3221470063675152&learning_id=6992591971338001). This **conflicts** with the essay FAQ above ("hanya dapat menetapkan peran"), and the conflict is unresolved.
- Old Tokopedia "Tambah Admin" flow:
  - Pick the "periode akses" (access period) and the number of admins.
  - Enter the admin's phone number, which must belong to an existing Tokopedia account, and check the shown name and email.
  - Pick one or more access types, then confirm with an OTP.
  - The invitee must confirm the invitation before acting.
  
  — [Everpro blog (2023)](https://everpro.id/blog/marketplace/kelola-akun-tokopedia/)
- TikTok Shop Seller Center has eight roles. "Main Administrator" can do almost everything "except for sensitive and private store information such as User Management, Account Information, Linked TikTok Accounts". — [TikTok Shop: Roles and permissions in Seller Center (Aug 2025)](https://ads.tiktok.com/help/article/roles-and-permissions-in-seller-center)

**Talenta**
- Settings → Users covers "User List Settings & Invite Users" and "Access Role Settings". — [Talenta: Settings Menu Overview](https://help-center.talenta.co/hc/en-us/articles/11420345176473-Settings-Menu-Overview)

### Inferences
- **Role templates for Unit Toko**: ship named default roles per organisation type, each with a one-line job description (Shopify/Shopee). Buying org: Pemesan cabang, Penyetuju belanja cabang, Admin organisasi pembeli. Koperasi: Kasir/penjual, Petugas gudang, Pemeriksa harga, Pemeriksa rilis faktur, Keuangan, Admin koperasi. Show a read-only "Lihat izin" matrix split by Lihat vs Lihat & Ubah (Tokopedia, Google "View privileges"). Assign roles only, never raw permissions, so the audit stays legible (Tokopedia FAQ).
- **Scope picker**: role + scope node in the org tree (buying org → wilayah → cabang → divisi; koperasi → toko → gudang → kanal), defaulting to the narrowest node the inviter controls ("Semua unit" should need an explicit choice). This mirrors Google's "All organizational units → Edit" and Entra admin units. Show inherited coverage, e.g. "berlaku untuk 12 cabang di Wilayah Jakarta".
- **Invitation**: email (or verified phone) → role + scope → optional "Berlaku sampai" end date (Entra expiry, old Tokopedia periode akses) → send. Show the status (Diundang / Aktif / Kedaluwarsa / Dinonaktifkan) and an activation deadline (Tokopedia: 120 hours), with "Kirim ulang" and "Batalkan undangan".
- **Maker/checker on access changes**: granting approver-capable or admin roles should require a second admin (Google multi-party approval), with a pending-request view showing "Diajukan oleh X" and actions Setujui / Tolak / Batalkan (own request). The proposer cannot approve their own grant.
- **Periodic access review ("Tinjau akses")**: give admins a cabang-scoped list with Pertahankan / Cabut / Tidak tahu, suggested revocations for accounts with no sign-in in 30+ days, and a required reason. Changes apply at review close (Entra).

### Gaps
- Shopify's English help pages returned 403. I could not verify Shopify's permission-preview UI or whether Shopify offers expiring staff access.
- No Google Workspace or Entra source on invitation emails or activation windows was retrieved. The 120-hour window is Tokopedia-specific.
- Duplicate-person linking ("Keterkaitan akun") was not researched. No sources were gathered on merge/link-identity UX (e.g. Salesforce duplicate rules, Entra account linking).

---

## 5. How are audit logs and maker/checker ("four-eyes") cues presented (who/what/when, before-after diff, filters, export; showing the proposer, blocking self-approval, pending changes)?

### Takeaway
Audit views share a shape:
- a filterable table of date/time, actor, service or category, activity and status,
- a detail drawer with target, correlation ID and old→new values,
- export to CSV or Sheets.

Maker/checker cues show the proposer and pending state explicitly (Google's "Requests submitted" vs requests to review; Coupa's approvers list and watchers; Amazon/Coupa rejection returning the item to the requester with comments). Self-approval is either configurable (Coupa) or structurally prevented by separate users (BNIdirect Maker/Approver/Releaser). Agent attribution is new: Google's admin log has a "By an agent" attribute.

### Cited Findings
- Entra audit log:
  - The table shows "Date and time", "Service that logged the occurrence", "Category and name of the activity" and "Status".
  - Selecting an entry shows the correlation ID, actor and target details, and "Where applicable, old and new values for the changed properties".
  - There are Directory and Custom Security tabs, the latter restricted to specific roles.
  
  — [Microsoft Learn: Entra audit logs](https://learn.microsoft.com/en-us/entra/identity/monitoring-health/concept-audit-logs)
- Google Workspace Admin log events:
  - Search attributes include Actor (email, or "License manager" / "Service account" / "Anonymous"), Actor application name (an optional column), "By an agent" ("Action was performed with an AI agent (True or False)"), Date (in the browser's time zone), Event (grouped by type, e.g. User Settings, Domain Settings), Domain name and Device ID/type.
  - "Export all" goes to Sheets or CSV, up to 100,000 rows (30 million with the security investigation tool).
  
  — [Google: Admin log events](https://support.google.com/a/answer/4579579?hl=en)
- Google multi-party approval separates the requests you created ("Requests submitted", cancellable) from requests you are authorised to review (approve/deny). Super-admin actions need "a second super administrator". — [Google: Multi-party approval](https://support.google.com/a/answer/13790448?hl=en)
- In Entra access reviews, a "Don't know" decision is recorded in audit logs, and reviewers' comments are visible to other reviewers. — [Microsoft Learn: perform access review](https://learn.microsoft.com/en-us/entra/id-governance/perform-access-review)
- Shopee Sub Akun "Aktivitas" shows member-account activity across the Sub Akun system and Seller Centre. — [Shopee Edu ID](https://seller.shopee.co.id/edu/article/8572)
- Shopee return/refund details include "Request history - A record of all actions taken by you and the buyer" and a "Discussion history". — [Shopee SG Edu: Processing return/refund requests](https://seller.shopee.sg/edu/article/7861/process-mall-return-refund)
- Mekari Expense: every submission, approval action, revision request and final clearance is logged "with a timestamp and approver identity". — [Mekari Expense blog](https://expense.mekari.com/en/blog/how-to-set-up-multi-layer-expense-approvals)
- Coupa: "Allow self-approvals" and "Notify manager on self approval" are explicit settings. The chain options "Skip Approvers prior to self" and "Auto Approve Self" show self-in-chain handling is a configuration concern. — [Coupa Approvals guide](https://get.coupa.com/rs/950-OLU-185/images/Approvals.pdf); [Coupa Approval Chain Import](https://docs.coupa.com/en/developer-documentation/coupa-core-flat-files-csv/flat-file-csv-import/approval-chain-import)
- BNIdirect enforces segregation through distinct Maker, Approver and Releaser users, each with their own credentials, plus an authority matrix and limits. — [BNI: BNIdirect](https://www.bni.co.id/en-us/home/bnidirect); [BNI Digital Disbursement](https://www.bni.co.id/en-us/corporate/solusi-wholesale/digital-disbursement-dan-payment-management)
- Failure mode: a forwarded approval email led to an audit trail recording a generic system account instead of the real actor. — [LawsonGuru forum](https://lawsonguru.com/forums/integration/ipa/process-flow-10-approval-from-forwarded-email) **[search extract]**

### Inferences
- **"Riwayat perubahan" table**: Waktu (WIB, with absolute timestamp on hover), Pelaku (name + role + scope, or "Sistem"/"Agen"), Tindakan, Objek (e.g. "Aturan belanja Cabang Bekasi"), Status. Filters for date range, pelaku, objek type, unit, and "Perlu persetujuan / Disetujui / Ditolak". Detail drawer with a Sebelum → Sesudah field diff (Entra old/new values), the request ID (correlation) and the linked approval decision. Offer "Ekspor CSV" (Google).
- **Four-eyes cues on every checker screen**:
  - "Diajukan oleh: Budi (Pemeriksa harga), 21 Okt 09.12".
  - A disabled "Setujui" button with the reason "Anda yang mengajukan perubahan ini" when viewer = proposer.
  - The pending change shown as a diff rather than just the new value.
  - "Menunggu persetujuan" badges on the live record so others know a change is pending (Google request states: pending / approved / denied / cancelled / expired).
- **Actor identity**: log the step-up-verified identity and the delegation context ("atas nama") to avoid the Lawson generic-actor failure. Add an actor-type field (human / system / agent), following Google's "By an agent".

### Gaps
- No source showed a side-by-side before/after diff UI specifically. Entra exposes old/new values in a detail pane, not a visual diff.
- No Indonesian ERP or accounting tool audit-log UI (e.g. Jurnal, Accurate) was researched.

---

## 6. What patterns do staff home dashboards and task inboxes use (to-do counters, work queues, grouping, counts, aging, priority)?

### Takeaway
Staff homes lead with a to-do summary of counts per action type, each count linking straight to a pre-filtered queue (Shopee "To Do List", Coupa "To Do's", Salesforce "Items to Approve", Shopee app "Order Status" tab). Queues then add urgency through due-date filters ("Due in 1 day / Due in 2 days"), "Key Action" filters, days-left-to-respond in an Actions column, oldest-item age, and red highlighting when an SLA is breached (RapidApproval). Linear adds triage controls: a Priority tab separating action-needed items from FYI, snooze, reminders, unread-first and badge counts. NN/g frames these as operational dashboards for time-sensitive action.

### Cited Findings
- Shopee Seller Centre homepage centre panel:
  - "待办清单 (To Do List)" gives an overview of orders needing shipment and pending refund or cancellation requests.
  - Then "My Data" (overview metrics) and "Marketing Centre" (current campaigns).
  - The left nav covers Order, Product, Marketing Centre, Finance, Data, Shop, Setting.
  
  — [LianLian Global: Shopee Seller Centre homepage (2023)](https://global.lianlianpay.com/article_platform_shopee/40-79774.html)
- The Shopee Seller Centre app home has an "Order Status" tab giving "the number of orders and their current status, including To Ship, Cancelled Orders, Return…, and Review". Notifications are organised into 7 categories: Order Updates, Ratings, Returns & Refunds, Listings, Finance Updates, Marketing Centre and Performance. — [Shopee PH Edu: Seller Center App](https://seller.shopee.ph/edu/article/26043)
- Shopee Return/Refund dashboard (Dec 2025):
  - "Priority" filter: All / "Due in 1 day" / "Due in 2 days".
  - "Key Action" filter: Reply to Buyer, Provide Evidence, Withdraw Parcel, Validate Item, Review Refund Approved by Shopee.
  - "Request Count" of pending requests.
  - "Actions" column "Shows required action(s) and the number of days left for you to respond".
  - Export to Excel.
  
  — [Shopee PH Edu: Navigating the Return/Refund Dashboard](https://seller.shopee.ph/edu/article/20188)
- The Shopee SG "Key Action" filter "will sort cases with the highest urgency at the top". "Reply To Buyer requests require your action. You can see the time you have left to respond under the Actions column." — [Shopee SG Edu](https://seller.shopee.sg/edu/article/7861/process-mall-return-refund)
- Indonesian Shopee Seller Centre uses the "Pengiriman → Perlu Dikirim" tab for orders needing shipment, plus a "Tertunda" sub-view. — [IDN Times](https://www.idntimes.com/tech/trend/cara-melacak-paket-shopee-00-2k3g7-z6vf3d) **[search extract]**
- Coupa homepage "To Do's" plus Inbox "To Do" view plus "Requiring My Approval" views per transaction tab. — [GitLab Handbook: Coupa guide](https://handbook.gitlab.com/handbook/business-technology/enterprise-applications/guides/coupa-guide/)
- Salesforce "Items to Approve" Home component. — [Salesforce Help](https://help.salesforce.com/s/articleView?id=platform.approvals_processing_approval_requests_parent.htm&language=en_US&type=5)
- RapidApproval home component: Approval Type / In Queue / Oldest Request, shown in red when items are aged beyond the per-type "Approval Reminder Days". — [RapidApproval guide](https://appexchange.salesforce.com/partners/servlet/servlet.FileDownload?file=00P3A00000YSbOKUA1)
- ServiceNow Approvals widget on the portal home, defaulting to 10 items with a configurable maximum. — [ServiceNow docs](https://github.com/ServiceNow/ServiceNowDocs/blob/australia/markdown/platform-user-interface/service-portal/approvals-widget.md)
- Linear Inbox:
  - The "Priority" tab "separates notifications that need your attention from other updates" and is customisable via a priority filter.
  - Display options include "Show snoozed", "Show unread first", badge count and preferred grouping.
  - Shortcuts: H snoozes, U marks read/unread, Backspace deletes, Shift+Backspace deletes all read.
  - Reminders take natural-language dates ("Next quarter", "For X days").
  
  — [Linear Docs: Inbox](https://linear.app/docs/inbox)
- NN/g definition: a dashboard is a collection of visualisations "in a single-page view that imparts at-a-glance information on which users can act quickly". "Operational dashboards aim to impart critical information quickly to users as they are engaged in time-sensitive tasks", in contrast to analytical dashboards. NN/g recommends length and 2D position (e.g. bars) for quantitative comparison. — [NN/g: Dashboards: Making Charts and Graphs Easier to Understand](https://www.nngroup.com/articles/dashboards-preattentive/)
- Talenta delegation creation offers "notification reminders" for requests in "Need My Approval". — [Talenta delegation settings](https://help-center.talenta.co/hc/en-us/articles/11388182960793-How-to-Create-and-Manage-Delegation-Settings)

### Inferences
- **"Pekerjaan saya" home**:
  - One card or row per work type the user's roles allow, e.g. "Permintaan cabang menunggu peninjauan · 7 · tertua 3 hari". Cards to include: Persetujuan harga, Rilis faktur, Rekening pemasok, Rilis stok, Persetujuan belanja.
  - Each count deep-links to the pre-filtered queue (Shopee/Coupa/Salesforce).
  - Sort cards by urgency (overdue count, then oldest age), not alphabetically. Mark "lewat batas" in red with the count of overdue items (RapidApproval).
  - Hide zero-count cards behind a "Semua beres" summary, or show them muted.
- **Queue-level urgency filters**: "Jatuh tempo hari ini / 1 hari / 2 hari" (Shopee Priority filter), "Tindakan utama" filters by required action (Shopee Key Action), and a "Sisa waktu" column (Shopee Actions column).
- **Separate "perlu tindakan" from "info"**: keep approvals and checks in the action list and status updates in a secondary tab (Linear Priority tab). A snooze ("Ingatkan saya") is acceptable for FYI items but should not hide items with an SLA.
- **Scope-aware counts**: counts must respect the user's role scope and active delegations ("termasuk 2 dari delegasi untuk Sari"). Show the active delegation banner here.
- **Operational dashboard rules (NN/g)**: numbers and bars over gauges or pies, and few charts, because the home is an action list, not analytics.

### Gaps
- The exact Indonesian label "Yang Perlu Dilakukan" on Shopee's ID Seller Centre home was not confirmed by any source. Only the English "To Do List" (via a Chinese-language guide) and the Shopee PH app layout were verified.
- No NN/g or Baymard article specific to task inboxes or approval queues was found in this pass. The NN/g dashboard article is from 2017 and is general.
- ServiceNow agent workspace work-queue (SLA timer) documentation was not retrieved, only the portal Approvals widget.
