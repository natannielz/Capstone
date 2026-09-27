import { test } from "node:test";
import assert from "node:assert/strict";
import { OperationJournal, submitTrackedRequest } from "../src/modules/administration/operation-journal";
import { AccessFailure, MutationLedger } from "../src/modules/administration/workflow";

class MemoryStorage {
  values = new Map<string, string>();
  get length() { return this.values.size; }
  key(index: number) { return [...this.values.keys()][index] ?? null; }
  getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.values.set(key, value); }
  removeItem(key: string) { this.values.delete(key); }
}

test("an interrupted command survives reload with only actor-owned recovery references", async () => {
  const storage = new MemoryStorage();
  const journal = new OperationJournal(() => storage);
  const ledger = new MutationLedger(() => "unchanged-key");
  const body = { userId: "22", reason: "Sensitive staffing rationale", additionalPermissions: ["payment.verify"] };
  await assert.rejects(submitTrackedRequest(ledger, journal, "9007199254740993", "proposal", "access.grant.propose", body, async () => {
    assert.equal(journal.list("9007199254740993").length, 1, "reference exists before any request");
    throw new AccessFailure(503, "commit_outcome_unknown");
  }));
  const reloaded = new OperationJournal(() => storage);
  assert.equal(reloaded.list("9007199254740993")[0].key, "unchanged-key");
  assert.deepEqual(reloaded.list("17"), []);
  assert.doesNotMatch([...storage.values.values()].join(""), /Sensitive|payment|additionalPermissions|userId/);
  const entry = reloaded.list("9007199254740993")[0];
  reloaded.confirmed(entry);
  assert.deepEqual(reloaded.list("9007199254740993"), []);
});

test("a denied retry retains the uncertain record until the server confirms or closes it", async () => {
  const journal = new OperationJournal(() => storage);
  const storage = new MemoryStorage();
  const ledger = new MutationLedger(() => "stable-key");
  const send = (error: AccessFailure) => submitTrackedRequest(ledger, journal, "1", "approve", "access.grant.approve", { id: "2" }, async () => { throw error; });
  await assert.rejects(send(new AccessFailure(503, "commit_outcome_unknown")));
  await assert.rejects(send(new AccessFailure(403, "stale_actor")));
  assert.equal(journal.list("1").length, 1);
  await submitTrackedRequest(ledger, journal, "1", "approve", "access.grant.approve", { id: "2" }, async key => { assert.equal(key, "stable-key"); return "confirmed"; });
  assert.equal(journal.list("1").length, 0);
});

test("storage failure prevents sending, while successful commands are not retried after cleanup failure", async () => {
  let sent = 0;
  const unavailable = new OperationJournal(() => { throw new Error("Storage disabled"); });
  await assert.rejects(submitTrackedRequest(new MutationLedger(), unavailable, "1", "propose", "access.membership.propose", {}, async () => { sent++; }), /recovery_storage_unavailable/);
  assert.equal(sent, 0);
  const storage = new MemoryStorage();
  const journal = new OperationJournal(() => storage);
  const ledger = new MutationLedger(() => "completed-key");
  const result = await submitTrackedRequest(ledger, journal, "1", "propose", "access.membership.propose", {}, async () => {
    storage.removeItem = () => { throw new Error("Storage unavailable during cleanup"); };
    return "committed";
  });
  assert.equal(result, "committed");
  assert.equal(ledger.isUncertain("propose"), false);
  assert.equal(journal.list("1").length, 1);
});

test("separate tabs cannot overwrite unrelated operation references or leak another actor's records", () => {
  const storage = new MemoryStorage();
  const first = new OperationJournal(() => storage);
  const second = new OperationJournal(() => storage);
  const a = first.remember("1", "access.grant.propose", "key-a");
  second.remember("1", "access.grant.propose", "key-b");
  second.remember("2", "access.grant.propose", "key-c");
  first.confirmed(a);
  assert.deepEqual(second.list("1").map(item => item.key), ["key-b"]);
  assert.deepEqual(second.list("2").map(item => item.key), ["key-c"]);
  storage.values.set("unit-toko:pending:v1:1:access.grant.propose:bad", JSON.stringify({ actorId: "2", operation: "access.grant.propose", key: "bad", createdAt: new Date().toISOString() }));
  assert.throws(() => first.list("1"), /recovery_storage_unavailable/);
});
