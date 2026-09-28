import { test } from "node:test";
import assert from "node:assert/strict";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { OperationJournal, reconcileOperationRecovery, operationRecoveryCapabilities, type OperationReference, type LegacyRecoveryAdapter, type OperationRecoveryAdapters } from "../src/modules/administration/operation-journal";
import { OperationRecovery } from "../src/modules/administration/OperationRecovery";

class MemoryStorage {
  values = new Map<string, string>();
  get length() { return this.values.size; }
  key(index: number) { return [...this.values.keys()][index] ?? null; }
  getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.values.set(key, value); }
  removeItem(key: string) { this.values.delete(key); }
}

const actorId = "9007199254740993";
const operations = ["person.review.propose", "person.review.approve", "person.review.reject", "person.review.withdraw"];
const entry: OperationReference = { actorId, operation: "person.review.propose", key: "person-once", createdAt: "2026-09-28T01:00:00.000Z" };

for (const operation of operations) test(operation + " retains only actor-owned recovery metadata", () => {
  const storage = new MemoryStorage();
  const journal = new OperationJournal(() => storage, () => new Date(entry.createdAt));
  assert.deepEqual(journal.remember(actorId, operation, entry.key), { ...entry, operation });
  assert.deepEqual(Object.keys(JSON.parse([...storage.values.values()][0])).sort(), ["actorId", "createdAt", "key", "operation"]);
  assert.deepEqual(new OperationJournal(() => storage).list(actorId), [{ ...entry, operation }]);
  assert.deepEqual(journal.list("1"), []);
});

test("the journal rejects unknown person operations", () => {
  const storage = new MemoryStorage();
  const journal = new OperationJournal(() => storage);
  for (const operation of ["person.review.close", "person.review.approved", "person.review.delete", "person.review.approve.extra", "person.profile.read"])
    assert.throws(() => journal.remember(actorId, operation, entry.key), /recovery_storage_unavailable/);
  assert.equal(storage.length, 0);
});

test("a person reference without its adapter stays locked and offers no generic close", () => {
  const storage = new MemoryStorage();
  const html = renderToStaticMarkup(createElement(OperationRecovery, { entries: [entry], journal: new OperationJournal(() => storage),
    disabled: false, onResolved() { assert.fail("render must not resolve an operation"); } }));
  assert.doesNotMatch(html, /Selesaikan dan tutup/);
  assert.match(html, /person-reviews/);
  assert.match(html, /<button[^>]*disabled[^>]*>Periksa hasil/);
});


const noLegacy: LegacyRecoveryAdapter = {
  async lookup() { assert.fail("person requests must never use access lookup"); },
  async close() { assert.fail("person requests must never use access resolution"); },
};
const result = { id: "9007199254740995", version: "9007199254740997" };
const committed = { state: "committed" as const, result, recordedAt: entry.createdAt };

function pending(operation = entry.operation) {
  const storage = new MemoryStorage();
  const journal = new OperationJournal(() => storage, () => new Date(entry.createdAt));
  return { storage, journal, reference: journal.remember(actorId, operation, entry.key) };
}

for (const operation of operations) test(operation + " uses only its adapter and clears only that actor's confirmed reference", async () => {
  const { journal, reference } = pending(operation);
  journal.remember("7", operation, entry.key);
  journal.remember(actorId, operation, "other-pending-key");
  let lookups = 0;
  const adapters: OperationRecoveryAdapters = { "person.review": { async lookup(actual) {
    lookups++;
    assert.deepEqual(actual, reference);
    return committed;
  } } };
  assert.deepEqual(await reconcileOperationRecovery(reference, journal, noLegacy, adapters), { state: "committed", result });
  assert.equal(lookups, 1);
  assert.deepEqual(journal.list(actorId).map(item => item.key), ["other-pending-key"]);
  assert.equal(journal.list("7").length, 1);
});

test("a null person receipt retains the journal and never automatically retries", async () => {
  const { journal, reference } = pending();
  let lookups = 0;
  const receipt = await reconcileOperationRecovery(reference, journal, noLegacy, { "person.review": { async lookup() { lookups++; return null; } } });
  assert.deepEqual(receipt, { state: "unconfirmed" });
  assert.equal(lookups, 1);
  assert.deepEqual(journal.list(actorId), [reference]);
});

test("missing adapters leave references unresolved without calling access lookup", async () => {
  const { journal, reference } = pending();
  await assert.rejects(reconcileOperationRecovery(reference, journal, noLegacy), /recovery_adapter_unavailable/);
  assert.deepEqual(journal.list(actorId), [reference]);
  assert.deepEqual(operationRecoveryCapabilities(reference.operation), { canCheck: false, canClose: false, unavailable: "adapter" });
});

test("unknown operation names cannot fall through to access recovery", async () => {
  const { journal, reference } = pending();
  for (const operation of ["payments.review.approve", "person.review.close", "identity.password.change", "access", "person.review.approve.extra"]) {
    await assert.rejects(reconcileOperationRecovery({ ...reference, operation }, journal, noLegacy), /recovery_operation_unsupported/);
    assert.deepEqual(operationRecoveryCapabilities(operation), { canCheck: false, canClose: false, unavailable: "operation" });
  }
  assert.deepEqual(journal.list(actorId), [reference]);
});

test("person references cannot close even when their lookup adapter is available", async () => {
  const { journal, reference } = pending();
  const adapters: OperationRecoveryAdapters = { "person.review": { async lookup() { assert.fail("unsupported close must not query or send"); } } };
  await assert.rejects(reconcileOperationRecovery(reference, journal, noLegacy, adapters, "reason"), /recovery_close_unsupported/);
  assert.deepEqual(journal.list(actorId), [reference]);
  assert.deepEqual(operationRecoveryCapabilities(reference.operation, adapters), { canCheck: true, canClose: false, unavailable: null });
});

test("an unexpected closed person receipt cannot erase an unresolved reference", async () => {
  const { journal, reference } = pending();
  const adapters = { "person.review": { async lookup() { return { state: "closed" }; } } } as unknown as OperationRecoveryAdapters;
  await assert.rejects(reconcileOperationRecovery(reference, journal, noLegacy, adapters), /recovery_receipt_invalid/);
  assert.deepEqual(journal.list(actorId), [reference]);
});

test("failed person lookups preserve the reference and propagate permission or session failures", async () => {
  const { journal, reference } = pending();
  const failure = new Error("session ended");
  await assert.rejects(reconcileOperationRecovery(reference, journal, noLegacy, { "person.review": { async lookup() { throw failure; } } }), error => error === failure);
  assert.deepEqual(journal.list(actorId), [reference]);
});

test("journal cleanup failure does not report recovery as resolved", async () => {
  const { journal, storage, reference } = pending();
  storage.removeItem = () => { throw new Error("storage unavailable"); };
  await assert.rejects(reconcileOperationRecovery(reference, journal, noLegacy, { "person.review": { async lookup() { return committed; } } }), /recovery_cleanup_failed/);
  assert.deepEqual(journal.list(actorId), [reference]);
});

test("the dispatched actor, operation and key stay bound while the lookup is pending", async () => {
  const { journal, reference } = pending();
  journal.remember("7", "person.review.reject", "other-key");
  const callerReference = { ...reference };
  let confirm!: (value: typeof committed) => void;
  const completion = new Promise<typeof committed>(resolve => { confirm = resolve; });
  const check = reconcileOperationRecovery(callerReference, journal, noLegacy, { "person.review": { async lookup(actual) {
    assert.ok(Object.isFrozen(actual));
    assert.deepEqual(actual, reference);
    return completion;
  } } });
  callerReference.actorId = "7"; callerReference.operation = "person.review.reject"; callerReference.key = "other-key";
  confirm(committed);
  await check;
  assert.deepEqual(journal.list(actorId), []);
  assert.equal(journal.list("7").length, 1);
});

for (const operation of ["access.grant.propose", "organization.branch.update", "identity.invitation.approve", "identity.recovery.request"]) {
  test(operation + " preserves built-in lookup and close with exact actor and key", async () => {
    const { journal, reference } = pending(operation);
    const calls: string[] = [];
    const legacy: LegacyRecoveryAdapter = {
      async lookup(actual) { calls.push("lookup"); assert.deepEqual(actual, reference); return { state: "notFound" }; },
      async close(actual, reason) { calls.push("close"); assert.deepEqual(actual, reference); assert.equal(reason, "Reviewed unresolved request"); return { state: "closed" }; },
    };
    const adapters: OperationRecoveryAdapters = { "person.review": { async lookup() { assert.fail("existing operations must retain their gateway"); } } };
    assert.deepEqual(operationRecoveryCapabilities(operation, adapters), { canCheck: true, canClose: true, unavailable: null });
    assert.deepEqual(await reconcileOperationRecovery(reference, journal, legacy, adapters), { state: "unconfirmed" });
    assert.equal(journal.list(actorId).length, 1);
    assert.deepEqual(await reconcileOperationRecovery(reference, journal, legacy, adapters, "  Reviewed unresolved request  "), { state: "closed" });
    assert.deepEqual(calls, ["lookup", "close"]);
    assert.deepEqual(journal.list(actorId), []);
  });
}

test("a confirmed built-in receipt still clears the matching reference", async () => {
  const { journal, reference } = pending("access.grant.approve");
  const receipt = await reconcileOperationRecovery(reference, journal, { async lookup() { return committed; }, close: noLegacy.close });
  assert.deepEqual(receipt, { state: "committed", result });
  assert.deepEqual(journal.list(actorId), []);
});

test("invalid reference metadata is rejected before contacting any adapter", async () => {
  const { journal, reference } = pending();
  const adapters: OperationRecoveryAdapters = { "person.review": { async lookup() { assert.fail("invalid references must not be sent"); } } };
  for (const malformed of [{ ...reference, actorId: "1e2" }, { ...reference, key: "" }, { ...reference, reason: "private context" }, { ...reference, createdAt: "invalid" }])
    await assert.rejects(reconcileOperationRecovery(malformed, journal, noLegacy, adapters), /recovery_reference_invalid/);
  assert.deepEqual(journal.list(actorId), [reference]);
});

test("the person screen can offer receipt lookup but never shared close or reconstructed retry", () => {
  const { journal, reference } = pending();
  const html = renderToStaticMarkup(createElement(OperationRecovery, { entries: [reference], journal, disabled: false,
    recoveryAdapters: { "person.review": { async lookup() { assert.fail("render must not send lookup"); } } }, onResolved() {} }));
  assert.doesNotMatch(html, /Selesaikan dan tutup|Kirim ulang|<button[^>]*disabled/);
  assert.match(html, /Periksa hasil/);
});

test("existing recovery keeps its close control while an unknown namespace stays locked", () => {
  const { journal, reference } = pending("organization.branch.update");
  const props = { journal, disabled: false, onResolved() {} };
  assert.match(renderToStaticMarkup(createElement(OperationRecovery, { ...props, entries: [reference] })), /Selesaikan dan tutup/);
  const unknown = renderToStaticMarkup(createElement(OperationRecovery, { ...props, entries: [{ ...reference, operation: "unknown.action" }] }));
  assert.doesNotMatch(unknown, /Selesaikan dan tutup/);
  assert.match(unknown, /belum didukung/);
  assert.match(unknown, /<button[^>]*disabled[^>]*>Periksa hasil/);
});


for (const operation of operations) test(operation + " resolves using its original metadata and explicit person adapter only", async () => {
  const { journal, reference, storage } = pending(operation);
  let closes = 0;
  const adapters: OperationRecoveryAdapters = { "person.review": { async lookup() { assert.fail("close must use explicit resolver"); },
    close: { allowed: candidate => candidate === operation, async resolve(actual, reason) {
      closes++; assert.ok(Object.isFrozen(actual)); assert.deepEqual(actual, reference); assert.equal(reason, "Periksa permintaan belum pasti");
      assert.deepEqual(Object.keys(JSON.parse([...storage.values.values()][0])).sort(), ["actorId", "createdAt", "key", "operation"]);
      return { state: "closed", result: null, recordedAt: entry.createdAt };
    } } } };
  assert.equal(operationRecoveryCapabilities(operation, adapters).canClose, true);
  assert.deepEqual(await reconcileOperationRecovery(reference, journal, noLegacy, adapters, "  Periksa permintaan belum pasti  "), { state: "closed" });
  assert.equal(closes, 1); assert.deepEqual(journal.list(actorId), []);
});

test("person closure requires explicit eligibility and a bounded nonblank reason before sending", async () => {
  const { journal, reference } = pending();
  const adapters: OperationRecoveryAdapters = { "person.review": { async lookup() { assert.fail("no lookup fallback"); }, close: {
    allowed: () => false, async resolve() { assert.fail("ineligible close must not send"); },
  } } };
  assert.equal(operationRecoveryCapabilities(reference.operation, adapters).canClose, false);
  await assert.rejects(reconcileOperationRecovery(reference, journal, noLegacy, adapters, "Reason"), /recovery_close_unavailable/);
  const eligible = { "person.review": { ...adapters["person.review"]!, close: { ...adapters["person.review"]!.close!, allowed: () => true } } };
  for (const reason of ["", " ", "x".repeat(1001)]) await assert.rejects(reconcileOperationRecovery(reference, journal, noLegacy, eligible, reason), /recovery_reason_invalid/);
  assert.deepEqual(journal.list(actorId), [reference]);
});

for (const state of ["committed", "closed"] as const) test("late " + state + " recovery replies cannot clear an old actor journal", async () => {
  const { journal, reference } = pending(); let current = true; let release!: () => void;
  const ready = new Promise<void>(resolve => { release = resolve; });
  const check = reconcileOperationRecovery(reference, journal, noLegacy, { "person.review": { async lookup() { await ready; return state === "committed" ? committed : { state: "closed", result: null, recordedAt: entry.createdAt }; } } }, undefined, () => current);
  current = false; release(); await assert.rejects(check, /recovery_request_stale/); assert.deepEqual(journal.list(actorId), [reference]);
});

test("closed person lookup unlocks while unconfirmed closure and malformed terminals remain retained", async () => {
  const { journal, reference } = pending();
  const unconfirmed: OperationRecoveryAdapters = { "person.review": { async lookup() { return null; }, close: { allowed: () => true, async resolve() { return null; } } } };
  assert.deepEqual(await reconcileOperationRecovery(reference, journal, noLegacy, unconfirmed, "Unknown outcome"), { state: "unconfirmed" });
  assert.deepEqual(journal.list(actorId), [reference]);
  for (const receipt of [{ state: "closed", result }, { state: "closed", result: null }, { state: "closed", result: null, recordedAt: "bad" }, { state: "committed", result: null, recordedAt: entry.createdAt }, { state: "committed", result: { id: 12, version: "1" }, recordedAt: entry.createdAt }, { state: "committed", result: { id: "12", version: 1 }, recordedAt: entry.createdAt }]) {
    const adapters = { "person.review": { async lookup() { return receipt; } } } as unknown as OperationRecoveryAdapters;
    await assert.rejects(reconcileOperationRecovery(reference, journal, noLegacy, adapters), /recovery_receipt_invalid/);
    assert.deepEqual(journal.list(actorId), [reference]);
  }
  assert.deepEqual(await reconcileOperationRecovery(reference, journal, noLegacy, { "person.review": { async lookup() { return { state: "closed", result: null, recordedAt: entry.createdAt }; } } }), { state: "closed" });
  assert.deepEqual(journal.list(actorId), []);
});

test("an explicit eligible person resolver offers closure while lookup remains available without MFA", () => {
  const { journal, reference } = pending(); const lookup = async () => null;
  const render = (allowed: boolean) => renderToStaticMarkup(createElement(OperationRecovery, { entries: [reference], journal, disabled: false, onResolved() {},
    recoveryAdapters: { "person.review": { lookup, close: { allowed: () => allowed, async resolve() { assert.fail("render cannot resolve"); } } } } }));
  assert.match(render(true), /Selesaikan dan tutup/);
  assert.doesNotMatch(render(false), /Selesaikan dan tutup/);
  assert.match(render(false), /Periksa hasil/);
});


test("a replaced recovery scope sends no request and retains its reference", async () => {
  const {journal,reference}=pending();
  await assert.rejects(reconcileOperationRecovery(reference,journal,noLegacy,{ "person.review": {async lookup(){assert.fail("stale scope cannot contact server");}}},undefined,()=>false),/recovery_request_stale/);
  assert.deepEqual(journal.list(actorId),[reference]);
});
