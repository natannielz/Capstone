import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

// Phase D task 3: the review panels load when a submission opens. They mount while the desk is still busy (open() runs
// inside perform()), so each loads once the first time it becomes unlocked — not at mount, where `locked` would
// skip the load. Automatic reads do not toggle the shared desk busy flag (three concurrent loads would otherwise
// unlock the desk early), and never eject the reviewer unless the session is lost or the account changed.
// Browser proof: verify-payment-verification-browser.mjs waits for the auto-loaded status without clicking.
const panels = [
  ["PaymentReviewActionPanel", "Muat ulang status pemeriksaan", "Muat status pemeriksaan"],
  ["PaymentDuplicatePanel", "Periksa ulang kecocokan pengajuan", "Periksa kecocokan pengajuan"],
  ["PaymentVerificationPanel", "Muat ulang hasil verifikasi", "Muat hasil verifikasi"],
] as const;

test("each payment review panel loads once when first unlocked, offers a reload, and keeps automatic failures from ejecting the reviewer", () => {
  for (const [name, reload, initial] of panels) {
    const source = readFileSync(`src/modules/billing/${name}.tsx`, "utf8");
    assert.match(source, /useEffect\(\(\) => \{ if \(locked \|\| autoLoaded\.current\) return; autoLoaded\.current = true;[^\n]*automatic[^\n]*\}, \[locked\]\);/,
      `${name} loads once when it first becomes unlocked`);
    assert.doesNotMatch(source, /automatic \*\/\)\); \}, \[\]\);/, `${name} must not load only at mount`);
    assert.match(source, /if \(!automatic\) onBusyChange\(true\)/, `${name} automatic loads leave the desk busy flag alone`);
    assert.ok(source.includes(`>${reload}<`), `${name} offers "${reload}"`);
    assert.ok(!source.includes(`>${initial}<`), `${name} still shows the manual first-load button`);
    assert.match(source, /autoLoadEndsSession\(failure\)/, `${name} guards onDenied for automatic loads`);
  }
});
