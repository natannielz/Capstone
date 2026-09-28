import { test } from "node:test";
import assert from "node:assert/strict";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { PersonReviewEditor } from "../src/modules/identity/PersonReviewPanels";
import type { PersonReviewGateway } from "../src/modules/identity/person-review-client";

const account = { userId: "9007199254741003", displayName: "Petugas Tebet", verifiedEmail: "tebet@example.test", status: "active" };
const result = { proposalId: "9007199254741001", version: "1", status: "pending", groupId: "9007199254740995", groupVersion: "9007199254740997", recordedAt: "2026-09-28T01:30:00Z" };
const summary = { result, requester: { ...account, userId: "41", displayName: "Pengaju Asli" }, reason: "Alasan semula yang perlu ditinjau", affectedAccountCount: "1", allowedActions: ["approve", "reject", "withdraw"] };
const props = { actorId: "42", permissions: ["person.review.read", "person.review.propose", "person.review.approve"], gateway: {} as PersonReviewGateway, locked: false, fresh: true,
  now: () => Date.parse("2026-09-28T02:00:00Z"), onFailure() { assert.fail("render must not report failure"); }, async onSubmit() { assert.fail("render must not submit"); }, onCancel() {} };

test("named target review has accessible reason and confirmation controls without identifier entry", () => {
  const html = renderToStaticMarkup(createElement(PersonReviewEditor, { ...props, selection: { kind: "target", target: { groupId: result.groupId, version: result.groupVersion, status: "disputed", accounts: [account] } } }));
  assert.match(html, /Petugas Tebet/); assert.match(html, /tebet@example.test/);
  assert.doesNotMatch(html, new RegExp([account.userId, result.groupId, result.proposalId].join("|")));
  assert.doesNotMatch(html, /type="number"|sourceReference|sourceSubject|digest/);
  assert.match(html, /textarea[^>]*required[^>]*maxLength="1000"/);
  assert.match(html, /input[^>]*type="checkbox"[^>]*disabled/);
  assert.match(html, /button[^>]*type="submit"[^>]*disabled/);
  assert.match(html, /Periksa dampak terbaru/);
});

test("read-only reviewers see the original request and completed independent decision", () => {
  const html = renderToStaticMarkup(createElement(PersonReviewEditor, { ...props, permissions: ["person.review.read"], selection: { kind: "proposal", detail: {
    summary: { ...summary, result: { ...result, version: "2", status: "approved" }, allowedActions: [] }, accounts: [account],
    decision: { actor: { ...account, userId: "43", displayName: "Peninjau Independen" }, reason: "Alasan keputusan tercatat", decidedAt: "2026-09-28T01:50:00Z" },
  } } }));
  for (const text of ["Pengaju Asli", "Alasan semula yang perlu ditinjau", "Peninjau Independen", "Alasan keputusan tercatat", "Keputusan yang tercatat"]) assert.match(html, new RegExp(text));
  assert.doesNotMatch(html, /<textarea|type="submit"|Setujui penangguhan|Tolak pengajuan/);
});

test("pending decisions are limited by both returned actions and explicit permissions", () => {
  const html = renderToStaticMarkup(createElement(PersonReviewEditor, { ...props, permissions: ["person.review.read", "person.review.approve"], selection: { kind: "proposal", detail: {
    summary: { ...summary, allowedActions: ["reject", "withdraw"] }, accounts: [account], decision: null,
  } } }));
  assert.match(html, /Tolak pengajuan/); assert.doesNotMatch(html, /Setujui penangguhan|Tarik pengajuan/);
});

test("missing fresh MFA prevents preview and submission before any mutation", () => {
  const html = renderToStaticMarkup(createElement(PersonReviewEditor, { ...props, fresh: false, selection: { kind: "target", target: { groupId: result.groupId, version: result.groupVersion, status: "active", accounts: [account] } } }));
  assert.match(html, /Verifikasi dua langkah terbaru diperlukan/);
  assert.match(html, /button[^>]*disabled[^>]*>Periksa dampak terbaru/);
  assert.match(html, /button[^>]*type="submit"[^>]*disabled/);
});