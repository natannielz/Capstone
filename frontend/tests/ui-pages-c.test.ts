import { test } from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync, readdirSync } from "node:fs";

// UI-PAGES-C (Task 3.8): the billing, administration, workspace home, operations, accounting and reporting staff pages on the shared
// primitives, scored against docs/quality/website-ready/ui-standard.md. Each block names the UX finding it pins (findings-ux.md
// U-036 … U-040, plus the shared rules U-003 … U-023 applied to these pages). Geometry that only a browser can measure (h1 x, control
// heights, overflow, axe) is checked with tmp/website-ready/ux/pages-c on the mocked pages; this file pins the source and the stylesheets.
const read = (path: string) => readFileSync(path, "utf8");
const css = (path: string) => existsSync(path) ? read(path).replace(/\/\*[\s\S]*?\*\//g, "") : "";
const source = (name: string) => read("src/modules/" + name);
/** Short failure messages: a whole page module in the assertion output hides the reason. */
const has = (text: string, pattern: RegExp, message?: string) => assert.ok(pattern.test(text), message ?? `expected ${pattern}`);
const lacks = (text: string, pattern: RegExp, message?: string) => { const found = text.match(pattern); assert.ok(!found, `${message ?? "unexpected " + pattern}: found "${found?.[0]}"`); };

const pages: Record<string, string> = {
  "/billing/invoices": "billing/InvoiceWorkspace.tsx",
  "/billing/payments": "billing/PaymentWorkspace.tsx",
  "/billing/invoice-releases": "invoice-releases/InvoiceReleaseWorkspace.tsx",
  "/billing/bank-statements": "billing/BankStatementWorkspace.tsx",
  "/billing/bank-receipts": "billing/BankReceiptWorkspace.tsx",
  "/billing/receiving-accounts": "billing/ReceivingAccountWorkspace.tsx",
  "/billing/payment-reviews": "billing/PaymentReviewWorkspace.tsx",
  "/administration/delegations": "administration/TemporaryDelegation.tsx",
  "/administration/person-reviews": "identity/PersonReviewAdministration.tsx",
  "/administration/access": "administration/AccessAdministration.tsx",
  "/administration/accounts": "identity/IdentityAdministration.tsx",
  "/administration/history": "administration/AccessHistory.tsx",
  "/administration/organizations": "organizations/OrganizationAdministration.tsx",
  "/operations/health": "operations/HealthWorkspace.tsx",
  "/operations/anomalies": "operations/AnomalyWorkspace.tsx",
  "/accounting/books": "accounting/AccountingBookWorkspace.tsx",
  "/reporting/accounts": "reporting/AccountingReportWorkspace.tsx",
};
/** The module stylesheets only these pages (and their own panels) compose. identity.module.css is shared with sign-in and "Akun saya". */
const sheets = [
  "src/modules/billing/invoice.module.css", "src/modules/billing/bank-statement.module.css", "src/modules/operations/health.module.css",
  "src/modules/operations/anomaly.module.css", "src/modules/reporting/reporting.module.css", "src/modules/organizations/organizations.module.css",
  "src/modules/identity/workspace-home.module.css",
];

function declarations(sheet: string, property: RegExp): Array<{ property: string; value: string }> {
  const found: Array<{ property: string; value: string }> = [];
  for (const match of sheet.matchAll(/(?:^|[{;\s])([a-z-]+)\s*:\s*([^;{}]+)/g))
    if (property.test(match[1])) found.push({ property: match[1], value: match[2].trim() });
  return found;
}
const scale = new Set(["0", "1px", "2px", "4px", "8px", "12px", "16px", "24px", "32px", "48px", "auto", "inherit"]);
function onScale(value: string): boolean {
  return value.replace(/!important/, "").replace(/calc\((?:[^()]|\([^()]*\))*\)/g, "calc()").trim().split(/\s+/)
    .every(part => scale.has(part.replace(/^-/, "")) || /^var\(--(space-\d+|gutter|control-h|header-h)\)$/.test(part) || /^-?calc\(\)$/.test(part)
      || /^\d+(\.\d+)?(em|ch|%|vh|dvh|fr)$/.test(part));
}
const typeScale = new Set(["12px", "13px", "14px", "15px", "16px", "18px", "20px", "24px", "28px", "inherit", "1em"]);
const radii = /^(0|6px|8px|12px|999px|50%|var\(--radius-(control|card|badge|pill)\))( (0|6px|8px|12px|var\(--radius-(control|card|badge|pill)\))){0,3}$/;
/** The local names a page gives the legacy shared stylesheet (`common`, or `styles` where the page imports only access.module.css). */
function legacyNames(code: string): string[] {
  return [...code.matchAll(/import\s+(\w+)\s+from\s+['"][^'"]*(?:administration\/|\.\/)access\.module\.css['"]/g)].map(match => match[1]);
}
/** A refresh label rendered inside a PageHeader `secondaryAction` (directly, or through the page's `header(action)` helper when the
 *  desk owns the refresh) or a Section `actions` slot — never loose in the body. */
const headerAction = (label: string) => new RegExp(`(?:(?:actions|secondaryAction)=\\{|header\\()(?:(?!<Section\\b|</Section>)[\\s\\S]){0,900}?>${label}</Button>`);

test("U-003: every page in scope renders its one h1 through PageHeader; the old header and brand link are gone", () => {
  for (const [route, file] of Object.entries(pages)) {
    const code = source(file);
    has(code, /from ['"]@\/components\/workspace-kit\/page-header['"]/, `${route}: imports PageHeader`);
    has(code, /<PageHeader\b/, `${route}: renders PageHeader`);
    lacks(code, /<h1\b/, `${route}: no hand-made h1`);
    for (const name of legacyNames(code)) lacks(code, new RegExp(`\\b${name}\\.(header|brand)\\b`), `${route}: no legacy header`);
  }
});

test("U-004/U-008/U-011/U-012/U-013/U-022: the legacy look-alikes give way to Section, DataList, Badge, Button, StateView and Facts", () => {
  // `.success` stays allowed: saved-result panels keep their h2, which rehearsals wait for by role heading.
  const retired = /\.(accountSection|queue|queueHeader|queueList|queueRow|selectedRow|state|empty|status|facts|primary|secondary|danger|alert|pagination)\b/;
  for (const [route, file] of Object.entries(pages)) {
    const code = source(file);
    for (const name of legacyNames(code)) lacks(code, new RegExp(`\\b${name}${retired.source}`), `${route}: legacy class`);
    lacks(code, /className=\{(styles|sheet)\.(facts|badge|status)\}/, `${route}: page-local facts or badge`);
    lacks(code, /<button\b[^>]*className=\{(common|styles)\.\w+\}/, `${route}: legacy button`);
    // A list's own "Belum ada …" paragraph beside the list: the list's empty state says it, and only when the read succeeded.
    lacks(code, /(!busy|!loading)[^{}]{0,60}(\.length === 0|\.length)\s*&&\s*<p>/, `${route}: hand-made empty text next to the list (one state per region)`);
    has(code, /<StateView\b/, `${route}: states through StateView`);
    lacks(code, /<p role="status">Memeriksa (akun|sesi)/, `${route}: the first load shows a visible skeleton, not a bare status line`);
  }
});

test("U-036: invoices — a failed read replaces the content; no live form says 'Belum ada penerimaan' under an error; refresh is in the header", () => {
  const code = source("billing/InvoiceWorkspace.tsx");
  has(code, /readFailed/, "invoices: the page knows a read failed (as opposed to a command)");
  has(code, /readFailed \? <StateView kind="error"/, "invoices: one error body instead of the form and the list");
  has(code, headerAction("Muat tagihan terbaru"), "invoices: 'Muat tagihan terbaru' is the header's secondary action");
  has(code, /<DataList\b[^>]*label="Invoice terbit"/, "invoices: the issued invoices are a DataList (empty only after a successful read)");
  has(code, /<form className="ui-form"/, "invoices: the form sits in the 720 px form column with 16/24 px gaps");
  has(code, /<Field id="invoice-reason" label="Alasan penerbitan">/, "invoices: the reason is a Field (label → 8 px → control)");
  has(code, /<Button type="submit" variant="primary"[^>]*>Tinjau invoice<\/Button>/, "invoices: the disabled primary is the kit Button (standard disabled tokens, no opacity)");
});

test("U-036: every billing page has refresh in a header, one state per list and forms in the form column", () => {
  const refresh: Record<string, string[]> = {
    "billing/PaymentWorkspace.tsx": ["Muat pembayaran terbaru", "Muat status bukti", "Muat tindak lanjut petugas"],
    "invoice-releases/InvoiceReleaseWorkspace.tsx": ["Riwayat terbaru", "Penerimaan terbaru"],
    "billing/BankStatementWorkspace.tsx": ["Muat daftar terbaru"],
    "billing/BankReceiptWorkspace.tsx": ["Muat data terbaru"],
    "billing/ReceivingAccountWorkspace.tsx": ["Muat daftar terbaru"],
    "billing/PaymentReviewWorkspace.tsx": ["Muat pengajuan terbaru"],
  };
  for (const [file, labels] of Object.entries(refresh)) {
    const code = source(file);
    for (const label of labels) has(code, headerAction(label), `${file}: "${label}" is a header/section action`);
    has(code, /<DataList\b|<DataTable\b/, `${file}: lists through DataList/DataTable`);
    has(code, /readFailed/, `${file}: a failed read is told apart from a failed command`);
  }
  for (const file of ["billing/PaymentWorkspace.tsx", "billing/BankStatementWorkspace.tsx", "billing/BankReceiptWorkspace.tsx", "billing/ReceivingAccountWorkspace.tsx", "invoice-releases/InvoiceReleaseWorkspace.tsx"]) {
    const code = source(file);
    has(code, /className="ui-form"/, `${file}: forms in the 720 px form column`);
    has(code, /<Field\b/, `${file}: labelled controls through Field`);
  }
  // Payment reviews keep their one table, now with the kit's empty and error states and the filter as a Toolbar Field.
  const reviews = source("billing/PaymentReviewWorkspace.tsx");
  has(reviews, /<DataTable\b[\s\S]{0,600}empty=/,"payment reviews: the table owns its empty state");
  has(reviews, /<Toolbar\b[\s\S]{0,300}<Field id="payment-queue-filter"/, "payment reviews: the view filter is a Toolbar Field");
});

test("U-037: administration desks — queue column ≥ 320 px, refresh in the header, no pager when empty, no glued controls, one state per error", () => {
  const delegations = source("administration/TemporaryDelegation.tsx");
  has(delegations, headerAction("Muat ulang sesi dan akses"), "delegations: refresh is a header action, not glued under the help paragraph");
  has(delegations, /<DataList\b[^>]*label="Delegasi dalam akses Anda"/, "delegations: the queue is a DataList (pager hides when empty)");
  has(delegations, /className="ui-desk"/, "delegations: the shared two-column desk (queue min 320 px, detail 1fr)");
  for (const file of ["identity/PersonReviewAdministration.tsx", "administration/AccessAdministration.tsx", "organizations/OrganizationAdministration.tsx", "accounting/AccountingBookWorkspace.tsx"])
    has(source(file), /className="ui-desk"/, `${file}: the shared desk`);
  const organizations = source("organizations/OrganizationAdministration.tsx");
  has(organizations, headerAction("Muat data terbaru"), "organizations: refresh is a header action");
  lacks(organizations, /Data organisasi belum tersedia[\s\S]{0,400}Muat data terbaru[\s\S]{0,400}role="alert"/, "organizations: one error body, not banner + button + card");
  for (const file of ["administration/AccessAdministration.tsx", "organizations/OrganizationAdministration.tsx", "identity/PersonReviewPanels.tsx", "identity/IdentityAdministrationPanels.tsx"])
    has(source(file), /<DataList\b/, `${file}: queue/directory as DataList`);
  // History entries keep their h3 per change (rehearsals find "Akun ditangguhkan" by role heading); the pager is the kit one.
  has(source("administration/AccessHistory.tsx"), /<Pagination label="Halaman riwayat"/, "history: the kit pager (hidden when everything fits)");
  // The desk itself: queue at least 320 px, detail 1fr, 24 px gap, one column below 1024 px.
  const foundation = read("src/app/foundation.css");
  has(foundation, /\.ui-desk\{display:grid;grid-template-columns:minmax\(320px,[^)]*\) minmax\(0,[^)]*\);[^}]*gap:var\(--space-6\)/, "foundation: .ui-desk");
  has(foundation, /@media \(max-width:1023px\)\{\.ui-desk\{grid-template-columns:minmax\(0,1fr\)\}\}/, "foundation: .ui-desk collapses below 1024 px");
});

test("U-038: workspace home — PageHeader h1, KPI tiles 16 px number→label with no stripe, section actions in headers, primitives not identity fields", () => {
  const account = source("identity/IdentityAccount.tsx");
  has(account, /<PageHeader\b/, "workspace: the h1 is PageHeader's (28/700 at the staff left edge)");
  const summary = source("identity/WorkSummary.tsx");
  has(summary, /<Section\b[^>]*title=\{heading\}[\s\S]{0,300}actions=\{<Button[^>]*>Muat ulang<\/Button>\}/, "work summary: 'Muat ulang' is the section header action");
  has(summary, /<StateView\b/, "work summary: states through StateView");
  const directory = source("identity/WorkspaceDirectory.tsx");
  has(directory, /<Section\b[^>]*title="Ruang kerja tersedia"/, "directory: a Section");
  has(directory, /<Field id="workspace-search" label="Cari tugas"/, "directory: the search is a kit Field (44 px), not the identity.module.css field");
  has(directory, /<StateView\b/, "directory: states through StateView");
  for (const file of ["identity/WorkSummary.tsx", "identity/WorkQueue.tsx", "identity/WorkspaceDirectory.tsx"])
    lacks(source(file), /identity\.module\.css/, `${file}: off identity.module.css`);
  const tiles = css("src/modules/identity/workspace-home.module.css");
  has(tiles, /\.tile\s*\{[^}]*gap:\s*var\(--space-4\)/, "tiles: number and label 16 px apart");
  lacks(tiles, /min-width:\s*(6[4-9]|[7-9]\d|\d{3})px/, "tiles: no reserved 64 px+ number column");
  lacks(tiles, /border(-(top|left|right|bottom))?\s*:\s*[2-9]px\s+solid/, "tiles: no stripe");
  has(tiles, /\.count\s*\{[^}]*font-size:\s*28px[^}]*font-weight:\s*700/, "tiles: KPI number 28/700");
});

test("U-039: operations — severity as a Badge beside each line, the health queue a DataTable that stacks on phones, facts via Facts, 44 px buttons", () => {
  const report = source("operations/HealthReportView.tsx");
  has(report, /<Badge\b/, "health: severity through Badge");
  has(report, /<DataTable\b/, "health: the queue table is a DataTable (stacked cards below 640 px, no horizontal scroll)");
  has(report, /<Facts\b/, "health: database and schema facts through Facts");
  lacks(report, /<table\b/, "health: no raw table");
  const health = source("operations/HealthWorkspace.tsx"), anomalies = source("operations/AnomalyWorkspace.tsx");
  lacks(health, /<button\b/, "health: buttons are the kit Button (44 px)");
  lacks(anomalies, /<button\b/, "anomalies: buttons are the kit Button (44 px, not 52)");
  has(anomalies, /<Field\b/, "anomalies: controls through Field");
  has(anomalies, /<Badge\b/, "anomalies: a flagged signal shows a Badge");
});

test("U-040: accounting and reporting — standard header, states and lists; selectors through Field; one primary per region", () => {
  const books = source("accounting/AccountingBookWorkspace.tsx"), reports = source("reporting/AccountingReportWorkspace.tsx");
  has(books, headerAction("Muat data terbaru"), "books: refresh is a header action");
  has(books, /<DataList\b/, "books: the book queue is a DataList");
  has(reports, headerAction("Muat ulang daftar buku"), "reporting: refresh is a header action");
  has(reports, /<Field\b/, "reporting: the book and period selectors are Fields");
  has(books, /<Field\b/, "books: fields through Field");
  // One primary per region: no two primary Buttons side by side in one action row.
  for (const [file, code] of [["books", books], ["reporting", reports]] as const)
    lacks(code, /variant="primary"[^<]*>[^<]*<\/Button>\s*<Button variant="primary"/, `${file}: two primaries in one action row`);
});

test("U-023/U-006/U-009/U-010: these pages' stylesheets use tokens only — no hex, no off-scale px, no stripes, no outline", () => {
  for (const file of sheets) {
    const sheet = css(file);
    assert.ok(sheet, `${file}: exists`);
    lacks(sheet, /#[0-9a-f]{3,8}\b/i, `${file}: hex colour`);
    lacks(sheet, /(?<![\w-])(white|black)(?![\w-])/, `${file}: named colour`);
    lacks(sheet, /var\(--workspace-/, `${file}: legacy palette token`);
    lacks(sheet, /\boutline\s*:/, `${file}: outline`);
    lacks(sheet, /border(-(top|left|right|bottom))?\s*:\s*[2-9]px\s+solid/, `${file}: stripe`);
    lacks(sheet, /\d(\.\d+)?rem\b/, `${file}: rem sizes (use the px scale)`);
    for (const { property, value } of declarations(sheet, /^(padding|margin)(-[a-z]+)*$|^(row-|column-)?gap$|^inset$/))
      assert.ok(onScale(value), `${file}: ${property}: ${value}`);
    for (const { value } of declarations(sheet, /^font-size$/)) assert.ok(typeScale.has(value), `${file}: font-size ${value}`);
    for (const { value } of declarations(sheet, /^font-weight$/)) assert.match(value, /^(400|600|700)$/, `${file}: font-weight ${value}`);
    for (const { value } of declarations(sheet, /^border(-[a-z]+)*-radius$/)) assert.match(value, radii, `${file}: radius ${value}`);
  }
});

test("U-040 and the shared one-primary rule: no source line and no action row of these pages holds two primary Buttons", () => {
  const modules = ["billing", "invoice-releases", "administration", "identity", "organizations", "operations", "accounting", "reporting", "procurement"];
  const files = modules.flatMap(dir => readdirSync("src/modules/" + dir).filter(name => name.endsWith(".tsx")).map(name => `src/modules/${dir}/${name}`));
  assert.ok(files.length > 40, "the scan covers the pages and their panels");
  for (const file of files) {
    const lines = read(file).split(/\r?\n/);
    lines.forEach((line, index) => {
      const primaries = line.match(/variant="primary"|variant=\{[^}]*'primary'[^}]*\}/g) ?? [];
      assert.ok(primaries.length <= 1, `${file}:${index + 1}: ${primaries.length} primary Buttons in one row`);
    });
    // Neighbours in the same action row, even across lines.
    lacks(read(file), /variant="primary"[^<]*>[^<]*<\/Button>\s*<Button variant="primary"/, `${file}: two primaries side by side`);
  }
});

// Procurement (U-040): the sixteen workspaces on the shared primitives, with D's logic (FIX-FLOW) untouched.
const procurementDesks: Record<string, string> = {
  "/procurement/requests": "ProcurementRequestWorkspace", "/procurement/purchase-orders": "PurchaseOrderWorkspace", "/procurement/receiving": "ProcurementReceivingWorkspace",
  "/procurement/funding": "ProcurementFundingWorkspace", "/procurement/suppliers": "SupplierWorkspace", "/procurement/supplier-banks": "SupplierBankWorkspace",
  "/procurement/supplier-claims": "SupplierClaimWorkspace", "/procurement/supplier-documents": "SupplierDocumentWorkspace", "/procurement/disbursements": "ProcurementDisbursementWorkspace",
  "/procurement/inspections": "ProcurementInspectionWorkspace", "/procurement/settlements": "ProcurementSettlementWorkspace", "/procurement/stock-receipts": "ProcurementStockReceiptWorkspace",
  "/procurement/stock-valuations": "ProcurementStockValuationWorkspace",
};
const procurementLists: Record<string, string> = {
  "/procurement/advance-returns": "ProcurementAdvanceReturnWorkspace", "/procurement/quality-acceptances": "ProcurementQualityAcceptanceWorkspace", "/procurement/supplier-payables": "ProcurementSupplierPayableWorkspace",
};
const procurement = (name: string) => source("procurement/" + name + ".tsx");

test("U-040 procurement: one h1 through PageHeader; the brand link, legacy header, alerts, states and look-alike buttons are gone", () => {
  for (const [route, name] of Object.entries({ ...procurementDesks, ...procurementLists })) {
    const code = procurement(name);
    has(code, /from ['"]@\/components\/workspace-kit\/page-header['"]/, `${route}: imports PageHeader`);
    has(code, /<PageHeader\b/, `${route}: renders PageHeader`);
    lacks(code, /<h1\b/, `${route}: no hand-made h1`);
    lacks(code, /\bstyles\.(header|brand|alert|empty|status|primary|secondary|danger|pagination|queue\w*|selectedRow)\b/, `${route}: legacy class`);
    lacks(code, /<button\b[^>]*className=\{styles\.\w+\}/, `${route}: legacy button`);
    has(code, /<StateView\b/, `${route}: states through StateView`);
    lacks(code, /<p role="status" className=\{styles\.success\}>/, `${route}: a saved notice is a StateView`);
    lacks(code, /href="\/identity\/account">Akun saya/, `${route}: no header link the frame already provides`);
    // D's logic stays: the kept-location helpers and the formatters are still used where they were.
  }
  has(procurement("ProcurementReceivingWorkspace"), /refreshAfterConfirmedSave\b[\s\S]*restoreKeptLocation\b|restoreKeptLocation\b[\s\S]*refreshAfterConfirmedSave\b/, "D's refresh helpers are still used");
});

test("U-040 procurement desks: queue column through DataList in the shared desk; the location choice and refresh are one Section with the refresh as its header action", () => {
  for (const [route, name] of Object.entries(procurementDesks)) {
    const code = procurement(name);
    has(code, /className="ui-desk"/, `${route}: the shared desk`);
    has(code, /<DataList\b/, `${route}: the queue is a DataList (one state, pager rule)`);
    has(code, /<Section title="Pilihan lokasi[^"]*"[^>]*actions=\{<Button variant="secondary"[^>]*>Muat (data|ulang)[^<]*<\/Button>\}/, `${route}: refresh is the location Section's header action`);
    has(code, /<Facts\b/, `${route}: facts through Facts`);
  }
});
