import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

// Phase D task 3: the review panels load when a submission opens (they are keyed by submission and revision, so a
// late reply from a previous submission is discarded on remount). The button becomes "Muat ulang …", and an automatic
// load never ejects the reviewer unless the session is lost or the account changed (browser proof:
// verify-payment-verification-browser.mjs waits for the auto-loaded status without clicking).
const panels = [
  ["PaymentReviewActionPanel", "Muat ulang status pemeriksaan", "Muat status pemeriksaan"],
  ["PaymentDuplicatePanel", "Periksa ulang kecocokan pengajuan", "Periksa kecocokan pengajuan"],
  ["PaymentVerificationPanel", "Muat ulang hasil verifikasi", "Muat hasil verifikasi"],
] as const;

test("each payment review panel loads on open, offers a reload, and keeps automatic failures from ejecting the reviewer", () => {
  for (const [name, reload, initial] of panels) {
    const source = readFileSync(`src/modules/billing/${name}.tsx`, "utf8");
    assert.match(source, /useEffect\(\(\) => \{ void Promise\.resolve\(\)\.then\(\(\) => [^;]*automatic[^;]*\); \}, \[\]\);/, `${name} loads once on mount`);
    assert.ok(source.includes(`>${reload}<`), `${name} offers "${reload}"`);
    assert.ok(!source.includes(`>${initial}<`), `${name} still shows the manual first-load button`);
    assert.match(source, /autoLoadEndsSession\(failure\)/, `${name} guards onDenied for automatic loads`);
  }
});
