import { test } from "node:test";
import assert from "node:assert/strict";
import { parsePersonReviewPage, parsePersonReviewTargets, parsePersonReviewDetail, parsePersonReviewPreview, personReviewCommand, PersonReviewPreviewSession, LatestPersonReviewRead,
  personReviewReceipt, validatePersonReviewOutcome, personReviewActionAllowed, personReviewMessage, personReviewNeedsRefresh, type PersonReviewDraft } from "../src/modules/identity/person-review-workflow";
import { AccessFailure, MutationLedger } from "../src/modules/administration/workflow";
import { OperationJournal, submitTrackedRequest } from "../src/modules/administration/operation-journal";

const now = Date.parse("2026-09-28T02:00:00Z");
const account = { userId: "9007199254740993", displayName: "Petugas Tebet", verifiedEmail: "tebet@example.test", status: "active" };
const target = { groupId: "9007199254740995", version: "9007199254740997", status: "disputed", accounts: [account] };
const result = { proposalId: "9007199254741001", version: "1", status: "pending", groupId: target.groupId, groupVersion: target.version, recordedAt: "2026-09-28T01:30:00Z" };
const summary = { result, requester: { ...account, userId: "41", displayName: "Pengaju" }, reason: "Hubungan akun perlu ditinjau", affectedAccountCount: "1", allowedActions: ["approve", "reject"] };
const draft: PersonReviewDraft = { actorId: "42", action: "propose", groupId: target.groupId, groupVersion: target.version, reason: "Hubungan akun perlu ditinjau" };
const preview = { reference: "12345678-1234-4123-8123-123456789abc", kind: "propose", expiresAt: "2026-09-28T02:05:00Z", target, proposal: null };
const entry = { actorId: draft.actorId, operation: "person.review.propose", key: "exact-key", createdAt: "2026-09-28T02:00:00Z" };

test("safe projections preserve large string identifiers and remove nested private evidence", () => {
  const page = parsePersonReviewPage({ total: "9007199254741003", items: [{ ...summary, secret: "source", requester: { ...summary.requester, subject: "private" }, result: { ...result, digest: "secret" } }] });
  assert.equal(page.total, "9007199254741003"); assert.equal(page.items[0].result.groupId, target.groupId);
  assert.doesNotMatch(JSON.stringify(page), /secret|private|digest|subject/);
  assert.deepEqual(parsePersonReviewTargets({ total: "1", items: [{ ...target, evidence: { secret: true } }] }), { total: "1", items: [target] });
  assert.deepEqual(parsePersonReviewDetail({ summary, accounts: [{ ...account, sourceReference: "private" }], decision: null }), { summary, accounts: [account], decision: null });
  assert.deepEqual(parsePersonReviewPreview({ ...preview, selectionDigest: "private" }), preview);
});

test("malformed or incomplete safe records cannot populate queues or previews", () => {
  for (const value of [{ total: 1, items: [] }, { total: "0", items: [summary] }, { total: "2", items: [summary, summary] }, { total: "1", items: [{ ...summary, result: { ...result, proposalId: 9007199254741001 } }] },
    { total: "1", items: [{ ...summary, allowedActions: ["access.approve"] }] }, { total: "1", items: [{ ...summary, affectedAccountCount: "257" }] }]) assert.throws(() => parsePersonReviewPage(value), /person_response_invalid/);
  for (const value of [{ ...target, version: "9223372036854775808" }, { ...target, accounts: [account, account] }, { ...target, accounts: [] }, { ...target, accounts: [{ ...account, verifiedEmail: null }] }])
    assert.throws(() => parsePersonReviewTargets({ total: "1", items: [value] }), /person_response_invalid/);
  assert.throws(() => parsePersonReviewDetail({ summary: { ...summary, affectedAccountCount: "2" }, accounts: [account], decision: null }));
  assert.throws(() => parsePersonReviewPreview({ ...preview, kind: "approve", proposal: null }));
  assert.throws(() => parsePersonReviewPreview({ ...preview, reference: "raw-database-reference" }));
});

test("preview acceptance is bound to the exact current actor, selection, revision, reason and attempt", () => {
  for (const change of [{ actorId: "43" }, { groupId: "99" }, { groupVersion: "2" }, { reason: "Alasan baru" }, { action: "approve" as const, proposalId: result.proposalId, proposalVersion: result.version }]) {
    const session = new PersonReviewPreviewSession(), ticket = session.begin(draft);
    assert.equal(session.accept(ticket, { ...draft, ...change }, preview, now), false);
    assert.equal(session.value, null);
  }
  const session = new PersonReviewPreviewSession();
  const old = session.begin(draft), latest = session.begin(draft);
  assert.equal(session.accept(old, draft, preview, now), false);
  assert.equal(session.accept(latest, draft, preview, now), true);
  session.invalidate(); assert.equal(session.value, null); assert.equal(session.accept(latest, draft, preview, now), false);
});

test("expired, mismatched and missing previews cannot produce a submission", () => {
  for (const candidate of [null, { ...preview, expiresAt: "invalid" }, { ...preview, expiresAt: new Date(now).toISOString() }, { ...preview, target: { ...target, groupId: "10" } }, { ...preview, target: { ...target, version: "2" } }])
    assert.throws(() => personReviewCommand(draft, candidate, now), /person_preview_stale/);
  const approveDraft: PersonReviewDraft = { ...draft, action: "approve", proposalId: result.proposalId, proposalVersion: "1" };
  const approval = { ...preview, kind: "approve", proposal: summary };
  assert.equal(personReviewCommand(approveDraft, approval, now).previewReference, preview.reference);
  assert.throws(() => personReviewCommand({ ...approveDraft, proposalVersion: "2" }, approval, now), /person_preview_stale/);
  assert.throws(() => personReviewCommand({ ...draft, reason: " " }, preview, now), /person_review_invalid/);
});

test("selection replacement and unmount abort reads and reject late transport results", () => {
  const requests = new LatestPersonReviewRead(), first = requests.begin(), second = requests.begin();
  assert.equal(first.signal.aborted, true); assert.equal(first.current(), false); assert.equal(second.current(), true);
  requests.cancel(); assert.equal(second.signal.aborted, true); assert.equal(second.current(), false);
});

test("decision controls require both server allowed actions and the explicit person permission", () => {
  assert.equal(personReviewActionAllowed("approve", summary, ["access.approve", "technical.configure"]), false);
  assert.equal(personReviewActionAllowed("approve", summary, ["person.review.approve"]), true);
  assert.equal(personReviewActionAllowed("withdraw", summary, ["person.review.propose"]), false);
  assert.equal(personReviewActionAllowed("approve", { ...summary, result: { ...result, status: "approved" } }, ["person.review.approve"]), false);
});

test("receipts confirm only the matching action status and strict string identities", () => {
  for (const [action, status] of Object.entries({ propose: "pending", approve: "approved", reject: "rejected", withdraw: "withdrawn" })) {
    assert.equal(personReviewReceipt({ state: "committed", recordedAt: result.recordedAt, result: { ...result, status } }, { ...entry, operation: "person.review." + action })?.result?.id, result.proposalId);
    assert.throws(() => personReviewReceipt({ state: "committed", recordedAt: result.recordedAt, result: { ...result, status: "unknown" } }, { ...entry, operation: "person.review." + action }));
  }
  assert.equal(personReviewReceipt({ state: "not_confirmed", recordedAt: null, result: null }, entry), null);
  for (const value of [{ state: "committed", recordedAt: result.recordedAt, result: null }, { state: "not_confirmed", recordedAt: null, result }, { state: "committed", recordedAt: result.recordedAt, result: { ...result, version: 1 } }, { state: "committed", recordedAt: result.recordedAt, result: { ...result, status: "approved" } }])
    assert.throws(() => personReviewReceipt(value, entry), /person_response_invalid/);
  assert.throws(() => validatePersonReviewOutcome(result, "propose", { groupId: "11" }));
  assert.throws(() => validatePersonReviewOutcome({ ...result, status: "approved" }, "approve", { proposalId: "12", groupId: target.groupId }));
});

test("unavailable governance is distinct from denied access and server-private error text", () => {
  assert.match(personReviewMessage(new AccessFailure(403, "person_governance_unavailable")), /belum diaktifkan/);
  assert.match(personReviewMessage(new AccessFailure(503, "person_selection_unavailable")), /tidak mengaktifkan/);
  assert.match(personReviewMessage(new AccessFailure(403, "permission_denied")), /belum memiliki izin/);
  assert.match(personReviewMessage(new AccessFailure(409, "version_conflict")), /pratinjau/);
});

test("an uncertain submission retries its frozen preview and reason without persisting them", async () => {
  const values = new Map<string, string>();
  const journal = new OperationJournal(() => ({ get length() { return values.size; }, key(index) { return [...values.keys()][index] ?? null; }, getItem(key) { return values.get(key) ?? null; }, setItem(key, value) { values.set(key, value); }, removeItem(key) { values.delete(key); } }));
  const ledger = new MutationLedger(() => entry.key), mutable = { ...draft }, frozen = personReviewCommand(mutable, preview, now);
  assert.ok(Object.isFrozen(frozen)); mutable.reason = "Edited after submit";
  const sent: unknown[] = [];
  const submit = (fail: boolean) => submitTrackedRequest(ledger, journal, draft.actorId, "person-command", entry.operation, frozen, async key => {
    sent.push({ key, ...frozen }); if (fail) throw new AccessFailure(503, "commit_outcome_unknown"); return result;
  });
  await assert.rejects(submit(true));
  assert.doesNotMatch([...values.values()].join(""), /previewReference|Hubungan|groupId|verifiedEmail/);
  await assert.rejects(submitTrackedRequest(ledger, journal, draft.actorId, "person-command", entry.operation, { ...frozen, reason: "changed" }, async () => assert.fail("changed requests must not be retried")), /resolve_previous_attempt/);
  await submit(false);
  assert.deepEqual(sent[0], sent[1]); assert.equal((sent[1] as Record<string, string>).reason, draft.reason); assert.equal(values.size, 0);
});

test("preview expiry and policy changes require refresh even when returned outside status 409", () => {
  for (const code of ["person_preview_expired", "person_preview_invalid", "person_policy_changed"]) {
    const failure = new AccessFailure(403, code);
    assert.equal(personReviewNeedsRefresh(failure), true);
    assert.match(personReviewMessage(failure), /pratinjau/);
  }
  assert.equal(personReviewNeedsRefresh(new AccessFailure(403, "permission_denied")), false);
});

test("recovery checks any proposal and group identity retained by an in-memory request", () => {
  const approval = { ...entry, operation: "person.review.approve" };
  const receipt = { state: "committed", recordedAt: result.recordedAt, result: { ...result, status: "approved", version: "2" } };
  assert.equal(personReviewReceipt(receipt, approval, { proposalId: result.proposalId, groupId: target.groupId })?.state, "committed");
  assert.throws(() => personReviewReceipt(receipt, approval, { proposalId: "93", groupId: target.groupId }), /person_response_invalid/);
  assert.throws(() => personReviewReceipt(receipt, approval, { proposalId: result.proposalId, groupId: "94" }), /person_response_invalid/);
});


test("missing allowed profiles ask for data repair without claiming governance is inactive", () => {
  const message = personReviewMessage(new AccessFailure(503, "person_profile_unavailable"));
  assert.match(message, /Data akun|diperbaiki/);
  assert.doesNotMatch(message, /belum diaktifkan|tidak mengaktifkan/);
});


test("changed actors are a session failure rather than a retryable stale preview", () => {
  const failure = new AccessFailure(409, "actor_changed");
  assert.equal(personReviewNeedsRefresh(failure), false);
  assert.match(personReviewMessage(failure), /Akun yang sedang masuk telah berubah/);
});


test("permanent recovery distinguishes strict terminal and unconfirmed envelopes", () => {
  assert.deepEqual(personReviewReceipt({ state: "closed_no_effect", result: null, recordedAt: result.recordedAt }, entry), { state: "closed", result: null, recordedAt: result.recordedAt });
  const malformed = [null, {}, { confirmed: true, result },
    { state: "closed_no_effect", result, recordedAt: result.recordedAt }, { state: "closed_no_effect", result: null, recordedAt: null },
    { state: "closed_no_effect", result: null, recordedAt: "invalid" }, { state: "not_confirmed", result: null, recordedAt: result.recordedAt },
    { state: "committed", result, recordedAt: null }, { state: "committed", result, recordedAt: "2026-09-28T02:00:00Z" },
    { state: "committed", result, recordedAt: result.recordedAt, confirmed: true }, { state: "closed", result: null, recordedAt: result.recordedAt }];
  for (const value of malformed) assert.throws(() => personReviewReceipt(value, entry), /person_response_invalid/);
  assert.equal(personReviewReceipt({ state: "committed", result, recordedAt: "2026-09-28T08:30:00+07:00" }, entry)?.state, "committed");
});
