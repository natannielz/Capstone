import { test } from "node:test";
import assert from "node:assert/strict";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { parseOperatorHealth, type OperatorHealth } from "../src/modules/operations/health-client";
import { healthFindings, overallTone } from "../src/modules/operations/health-findings";
import { HealthReportView } from "../src/modules/operations/HealthReportView";
import { destinationLabel, identityDestinations, identityReturnPath } from "../src/modules/identity/identity-workflow";

const queue = { pending: 0, retrying: 0, dead: 0, leased: 0, oldestWaitingAt: null };
const healthy: OperatorHealth = { observedAt: "2026-10-20T10:00:00Z", outbox: queue, ocr: queue, lastDraftPurgeAt: "2026-10-20T02:00:00Z", overdueDraftPurges: 0,
  schemaVersion: 118, targetSchemaVersion: 118, databaseWritable: true, replicationLagSeconds: 0.4 };

// Q10: the operator screen turns counts and ages into plain findings; the worst one sets the page's state.
test("the health response is parsed strictly: counts, ages and states only", () => {
  assert.deepEqual(parseOperatorHealth(healthy), healthy);
  assert.throws(() => parseOperatorHealth({ ...healthy, customer: "PT Rahasia" }), "no unexpected fields");
  assert.throws(() => parseOperatorHealth({ ...healthy, ocr: { ...queue, dead: -1 } }));
  assert.throws(() => parseOperatorHealth({ ...healthy, observedAt: "kemarin" }));
});

test("a healthy system reads as good, and each problem raises its own finding", () => {
  assert.equal(overallTone(healthFindings(healthy)), "success");
  const failed = healthFindings({ ...healthy, ocr: { ...queue, dead: 2, oldestWaitingAt: "2026-10-20T05:00:00Z" } });
  assert.equal(overallTone(failed), "danger");
  assert.ok(failed.some(finding => finding.tone === "danger" && /2 pekerjaan OCR gagal/.test(finding.text)));
  const slow = healthFindings({ ...healthy, outbox: { ...queue, pending: 4, oldestWaitingAt: "2026-10-20T09:10:00Z" } });
  assert.equal(overallTone(slow), "warning", "work waiting 50 minutes needs attention");
  assert.equal(overallTone(healthFindings({ ...healthy, schemaVersion: 117 })), "danger", "an unfinished migration");
  assert.equal(overallTone(healthFindings({ ...healthy, databaseWritable: false })), "danger");
  assert.equal(overallTone(healthFindings({ ...healthy, replicationLagSeconds: 45 })), "warning");
  assert.ok(healthFindings({ ...healthy, replicationLagSeconds: null }).some(finding => finding.tone === "neutral" && /replikasi/i.test(finding.text)));
  assert.equal(overallTone(healthFindings({ ...healthy, overdueDraftPurges: 3 })), "warning", "the purge is not keeping up");
  assert.equal(overallTone(healthFindings({ ...healthy, lastDraftPurgeAt: null })), "success", "no purge yet is not a problem by itself");
});

test("the report view lists each queue with its counts and the findings as status badges", () => {
  const html = renderToStaticMarkup(createElement(HealthReportView, { report: { ...healthy, ocr: { ...queue, dead: 1, oldestWaitingAt: "2026-10-20T07:00:00Z" } } }));
  assert.match(html, /<table[^>]*>[\s\S]*<caption[^>]*>Jumlah pekerjaan per antrean<\/caption>/);
  assert.match(html, /<th scope="row"[^>]*>OCR dokumen<\/th>/);
  assert.match(html, /data-tone="danger" class="status-badge"/);
  assert.match(html, /Versi skema 118 dari 118/);
  assert.doesNotMatch(html, /Rahasia|undefined|NaN/);
});

test("operators with technical configuration reach the health page from their account and after sign-in", () => {
  assert.ok(identityDestinations(["technical.configure"]).some(item => item.href === "/operations/health"));
  assert.equal(identityDestinations(["operations.anomaly.read", "access.audit.read"]).some(item => item.href === "/operations/health"), false);
  assert.equal(destinationLabel("/operations/health"), "Kesehatan sistem");
  assert.equal(identityReturnPath("/operations/health"), "/operations/health");
});
