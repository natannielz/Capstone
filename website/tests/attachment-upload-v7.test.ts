import {test} from "node:test";
import assert from "node:assert/strict";
import {mkdtemp, readdir, rm} from "node:fs/promises";
import {tmpdir} from "node:os";
import {basename, dirname, join, resolve} from "node:path";
import {setImmediate} from "node:timers/promises";
import type {Actor} from "../lib/domain/accounts";
import {DomainError, type Command, type CommandResult} from "../lib/domain/model";
import {emptyState} from "../lib/domain/seed";
import {runCommand} from "../lib/domain/engine";
import {readAttachmentUpload, saveAttachment, type AttachmentUpload} from "../lib/server/attachment-upload";
import {files as localFiles} from "../lib/server/files";

const pdf = (text = "simulated proof") => new TextEncoder().encode(`%PDF-1.7\n${text}`).buffer;
const upload = (changes: Partial<AttachmentUpload> = {}): AttachmentUpload => ({scope: "payment", targetId: "payment-a", name: "proof.pdf", content: pdf(), ...changes});
const denied = (error: unknown) => error instanceof DomainError && error.status === 403;

// Private Blob's immutable put/existence semantics are simulated without touching
// any live storage. Actual attachmentContext, runCommand, dates and audit run here.
function fixture() {
  const f = {
    state: emptyState(), objects: new Map<string, ArrayBuffer>(), ledger: new Map<string, {fingerprint: string; result: CommandResult}>(),
    gets: 0, puts: 0, cancellations: 0, deletions: 0, executions: 0, conflicts: 0,
    beforeExecute: undefined as undefined | ((command: Command) => Promise<void>),
    afterPut: undefined as undefined | (() => void),
    putFailure: "" as "" | "before" | "after",
    executeFailure: "" as "" | "before" | "after",
  };
  const a: Actor = {id: "customer-a", name: "A", role: "customer", divisionId: null, active: true};
  const b: Actor = {...a, id: "customer-b", name: "B"};
  const finance: Actor = {...a, id: "finance", role: "penagihan"};
  f.state.users = [a, b, finance];
  for (const [id, customerId] of [["payment-a", a.id], ["payment-a2", a.id], ["payment-b", b.id]]) {
    f.state.payments.push({id, customerId, divisionId: null, amount: 1000, date: "2026-01-01", reference: "simulation", payer: "Demo", note: "", status: "recorded", verifiedBy: null, evidence: "", createdBy: customerId});
  }
  f.state.orders.push({id: "order-a", number: "ORDER-A", customerId: a.id, divisionId: null, createdBy: a.id, createdAt: "2026-01-01T00:00:00Z", neededAt: "2026-01-02", address: "Demo", note: "", status: "approved", origin: "customer"});
  // Same target string in another scope must still have a different key.
  f.state.shipments.push({id: "payment-a", number: "SHIP-A", orderId: "order-a", courierId: "courier", vehicle: "Demo", date: "2026-01-02", status: "dispatched", receiver: "", receivedAt: "", evidence: "", note: ""});
  const files = {
    async get(id: string) {
      f.gets++;
      if (!f.objects.has(id)) return null;
      return {body: new ReadableStream<Uint8Array>({cancel() {f.cancellations++;}})};
    },
    async put(id: string, content: ArrayBuffer) {
      f.puts++;
      if (f.putFailure === "before") throw new Error("Storage unavailable");
      if (f.objects.has(id)) throw new Error("Blob already exists; overwrite denied");
      f.objects.set(id, content.slice(0));
      f.afterPut?.();
      if (f.putFailure === "after") throw new Error("Put response lost after storing");
    },
    async delete(id: string) {f.deletions++; f.objects.delete(id);},
  };
  const dependencies = {
    files,
    async loadState() {return structuredClone(f.state);},
    async execute(actor: Actor, command: Command) {
      f.executions++;
      await f.beforeExecute?.(command);
      if (f.executeFailure === "before") throw new Error("Transaction did not commit");
      const fingerprint = JSON.stringify(command);
      const existing = f.ledger.get(command.id);
      if (existing) {
        if (existing.fingerprint !== fingerprint) {f.conflicts++; throw new DomainError("Identitas tindakan sudah digunakan untuk permintaan berbeda.", 409);}
        return existing.result;
      }
      const outcome = runCommand(f.state, f.state.users.find(user => user.id === actor.id)!, command);
      f.state = outcome.state;
      f.ledger.set(command.id, {fingerprint, result: outcome.result});
      if (f.executeFailure === "after") throw new Error("Commit response lost");
      return outcome.result;
    },
  };
  return {f, a, b, finance, dependencies};
}

test("attachment retry after a lost client response returns first metadata after rename without another audit", async () => {
  const {f, a, dependencies} = fixture();
  const first = await saveAttachment(a, upload(), dependencies);
  const snapshot = structuredClone(f.state.attachments[0]);
  const retry = await saveAttachment(a, upload({name: "renamed-on-retry.pdf"}), {...dependencies, now: () => new Date("2030-12-31T23:59:59Z")});
  assert.deepEqual(retry, first);
  assert.deepEqual(f.state.attachments, [snapshot]);
  assert.equal(f.state.audits.length, 1);
  assert.equal(f.executions, 1);
  assert.equal(f.puts, 1);
  assert.equal(f.cancellations, 1);
});

test("parallel identical uploads recover no-overwrite and command fingerprint conflicts across filename/date changes", async () => {
  const {f, a, dependencies} = fixture();
  let entered = 0;
  let release!: () => void;
  const gate = new Promise<void>(resolve => {release = resolve;});
  f.beforeExecute = async () => {if (++entered === 2) release(); await gate;};
  const results = await Promise.all([
    saveAttachment(a, upload({name: "first.pdf"}), {...dependencies, now: () => new Date("2026-01-01T00:00:00Z")}),
    saveAttachment(a, upload({name: "second.pdf"}), {...dependencies, now: () => new Date("2026-01-02T00:00:00Z")}),
  ]);
  assert.deepEqual(results[0], results[1]);
  assert.equal(f.puts, 2);
  assert.equal(f.objects.size, 1);
  assert.equal(f.state.attachments.length, 1);
  assert.equal(f.state.audits.length, 1);
  assert.equal(f.conflicts, 1);
  assert.equal(f.cancellations, 1);
  assert.equal(f.deletions, 0);
  assert.deepEqual(f.objects.get(results[0].id), pdf());
});

test("attachment identity isolates actor, target, scope and byte content", async () => {
  const {f, a, b, finance, dependencies} = fixture();
  const results = await Promise.all([
    saveAttachment(a, upload(), dependencies),
    saveAttachment(a, upload({targetId: "payment-a2"}), dependencies),
    saveAttachment(finance, upload(), dependencies),
    saveAttachment(a, upload({content: pdf("different bytes")}), dependencies),
    saveAttachment(a, upload({scope: "receipt"}), dependencies),
    saveAttachment(b, upload({targetId: "payment-b"}), dependencies),
  ]);
  assert.equal(new Set(results.map(result => result.id)).size, 6);
  assert.equal(f.state.attachments.length, 6);
  assert.equal(f.objects.size, 6);
});

test("foreign, inactive and unknown-role uploads are rejected before any storage lookup or write", async () => {
  const {f, a, dependencies} = fixture();
  await assert.rejects(saveAttachment(a, upload({targetId: "payment-b"}), dependencies), denied);
  f.state.users[0].active = false;
  await assert.rejects(saveAttachment(a, upload(), dependencies), denied);
  f.state.users[0].active = true;
  f.state.users[0].role = "invented" as Actor["role"];
  await assert.rejects(saveAttachment(a, upload(), dependencies), denied);
  assert.equal(f.gets, 0);
  assert.equal(f.puts, 0);
  assert.equal(f.executions, 0);
});

test("authorization is refreshed before metadata write and before returning an existing upload", async () => {
  const {f, a, dependencies} = fixture();
  f.afterPut = () => {f.state.users[0].active = false;};
  await assert.rejects(saveAttachment(a, upload(), dependencies), denied);
  assert.equal(f.executions, 0);
  assert.equal(f.objects.size, 1);
  assert.equal(f.deletions, 0);
  f.afterPut = undefined;
  f.state.users[0].active = true;
  await saveAttachment(a, upload(), dependencies);
  f.state.users[0].active = false;
  const gets = f.gets;
  await assert.rejects(saveAttachment(a, upload(), dependencies), denied);
  assert.equal(f.gets, gets);
});

test("a retry restores a missing object without changing committed metadata or audit", async () => {
  const {f, a, dependencies} = fixture();
  const first = await saveAttachment(a, upload(), dependencies);
  const metadata = structuredClone(f.state.attachments);
  f.objects.clear();
  assert.deepEqual(await saveAttachment(a, upload({name: "restored.pdf"}), dependencies), first);
  assert.deepEqual(f.objects.get(first.id), pdf());
  assert.deepEqual(f.state.attachments, metadata);
  assert.equal(f.state.audits.length, 1);
  assert.equal(f.puts, 2);
  assert.equal(f.executions, 1);
});

test("failed storage prevents metadata, while a lost put response recovers the existing object and cancels its stream", async () => {
  const {f, a, dependencies} = fixture();
  f.putFailure = "before";
  await assert.rejects(saveAttachment(a, upload(), dependencies), /Storage unavailable/);
  assert.equal(f.executions, 0);
  assert.equal(f.objects.size, 0);
  f.putFailure = "after";
  const result = await saveAttachment(a, upload(), dependencies);
  assert.equal(f.state.attachments[0].id, result.id);
  assert.equal(f.cancellations, 1);
  assert.equal(f.deletions, 0);
});

test("ambiguous committed metadata is recovered without deleting the canonical document", async () => {
  const {f, a, dependencies} = fixture();
  f.executeFailure = "after";
  const result = await saveAttachment(a, upload(), dependencies);
  assert.equal(f.state.attachments[0].id, result.id);
  assert.deepEqual(f.objects.get(result.id), pdf());
  assert.equal(f.deletions, 0);
  assert.equal(f.state.audits.length, 1);
});

test("uncommitted metadata failure retains one reusable orphan and retry commits it without another put", async () => {
  const {f, a, dependencies} = fixture();
  f.executeFailure = "before";
  await assert.rejects(saveAttachment(a, upload(), dependencies), /did not commit/);
  assert.equal(f.state.attachments.length, 0);
  assert.equal(f.objects.size, 1);
  assert.equal(f.deletions, 0);
  f.executeFailure = "";
  await saveAttachment(a, upload(), dependencies);
  assert.equal(f.puts, 1);
  assert.equal(f.state.attachments.length, 1);
});

test("upload MIME sniff and exact 4 MB limit precede storage writes", async () => {
  const {f, a, dependencies} = fixture();
  for (const content of [new ArrayBuffer(0), new ArrayBuffer(4 * 1024 * 1024 + 1), new TextEncoder().encode("not actually a PDF").buffer]) {
    await assert.rejects(saveAttachment(a, upload({content}), dependencies), DomainError);
  }
  assert.equal(f.gets, 0);
  const large = new Uint8Array(4 * 1024 * 1024);
  large.set(new TextEncoder().encode("%PDF-"));
  await saveAttachment(a, upload({name: "../misleading.jpg", content: large.buffer}), dependencies);
  await saveAttachment(a, upload({content: new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]).buffer}), dependencies);
  await saveAttachment(a, upload({content: new Uint8Array([255, 216, 255, 1]).buffer}), dependencies);
  assert.deepEqual(f.state.attachments.map(attachment => attachment.mime), ["application/pdf", "image/png", "image/jpeg"]);
  assert.equal(f.state.attachments[0].name, ".._misleading.jpg");
});

test("attachment date uses the first open month while creation timestamp remains the actual event time", async () => {
  const {f, finance, dependencies} = fixture();
  f.state.periods.push({id: "2026-09", revision: 1, status: "closed", approvedRevision: 1, approvedBy: "finance", submittedBy: "finance", closedBy: "finance", closedAt: "2026-09-30", snapshot: {preserved: true}});
  const before = Date.now();
  await saveAttachment(finance, upload(), {...dependencies, now: () => new Date("2026-09-15T15:00:00Z")});
  assert.equal(f.state.audits[0].date, "2026-10-01");
  assert.ok(Date.parse(f.state.attachments[0].createdAt) >= before);
  assert.ok(Date.parse(f.state.attachments[0].createdAt) <= Date.now());
  assert.deepEqual(f.state.periods[0].snapshot, {preserved: true});
});

test("multipart parsing preserves bytes and rejects malformed or oversized files", async () => {
  const form = new FormData();
  form.set("file", new File([pdf()], "demo.pdf")); form.set("scope", "payment"); form.set("targetId", "payment-a");
  const parsed = await readAttachmentUpload(new Request("https://unit.test/api/attachments", {method: "POST", body: form}));
  assert.deepEqual(parsed, upload({name: "demo.pdf"}));
  await assert.rejects(readAttachmentUpload(new Request("https://unit.test", {method: "POST", body: "not multipart"})), /Format unggahan/);
  form.set("file", new File([new ArrayBuffer(4 * 1024 * 1024 + 1)], "too-big.pdf"));
  await assert.rejects(readAttachmentUpload(new Request("https://unit.test", {method: "POST", body: form})), /maksimal 4 MB/);
});

test("bounded request reader cancels oversized advertised and chunked bodies and releases its lock", async () => {
  for (const advertised of [true, false]) {
    let cancelled = 0;
    const stream = new ReadableStream<Uint8Array>({pull(controller) {controller.enqueue(new Uint8Array(4 * 1024 * 1024 + 20001));}, cancel() {cancelled++;}});
    const init = {method: "POST", body: stream, duplex: "half", headers: advertised ? {"content-length": "5000000"} : undefined};
    const request = new Request("https://unit.test", init);
    await assert.rejects(readAttachmentUpload(request), error => error instanceof DomainError && error.status === 413);
    assert.equal(cancelled, 1);
    assert.equal(stream.locked, false);
  }
});

async function isolatedLocalFiles(run: () => Promise<void>) {
  const oldCwd = process.cwd(), oldVercel = process.env.VERCEL, oldToken = process.env.BLOB_READ_WRITE_TOKEN;
  const directory = await mkdtemp(join(tmpdir(), "unit-toko-files-v7-"));
  try {
    process.chdir(directory);
    delete process.env.VERCEL; delete process.env.BLOB_READ_WRITE_TOKEN;
    await run();
  } finally {
    process.chdir(oldCwd);
    if (oldVercel === undefined) delete process.env.VERCEL; else process.env.VERCEL = oldVercel;
    if (oldToken === undefined) delete process.env.BLOB_READ_WRITE_TOKEN; else process.env.BLOB_READ_WRITE_TOKEN = oldToken;
    const target = resolve(directory);
    assert.equal(dirname(target), resolve(tmpdir()));
    assert.ok(basename(target).startsWith("unit-toko-files-v7-"));
    await rm(target, {recursive: true, force: true});
  }
}

test("local immutable put publishes complete bytes atomically and cleans only its own temporary files", async () => {
  await isolatedLocalFiles(async () => {
    const content = new Uint8Array(4 * 1024 * 1024).fill(97).buffer;
    const options = {httpMetadata: {contentType: "application/pdf"}};
    let completed = 0;
    const writes = Array.from({length: 16}, () => localFiles.put("same-key", content, options).finally(() => {completed++;}));
    const settled = Promise.allSettled(writes);
    while (completed < writes.length) {
      const file = await localFiles.get("same-key");
      if (file) assert.deepEqual(await new Response(file.body).arrayBuffer(), content);
      await setImmediate();
    }
    const outcomes = await settled;
    assert.equal(outcomes.filter(outcome => outcome.status === "fulfilled").length, 1);
    assert.equal(outcomes.filter(outcome => outcome.status === "rejected" && (outcome.reason as NodeJS.ErrnoException).code === "EEXIST").length, 15);
    await assert.rejects(localFiles.put("same-key", pdf("must not replace stored bytes"), options), {code: "EEXIST"});
    assert.deepEqual(await new Response((await localFiles.get("same-key"))!.body).arrayBuffer(), content);
    await localFiles.put("avatar-unique", pdf(), options);
    assert.deepEqual((await readdir(join(process.cwd(), ".data", "files"))).sort(), ["avatar-unique", "same-key"]);
  });
});

test("parallel uploads using the real local store produce one readable document and one metadata record", async () => {
  await isolatedLocalFiles(async () => {
    const {f, a, dependencies} = fixture();
    const results = await Promise.all(Array.from({length: 8}, (_, index) => saveAttachment(a, upload({name: `retry-${index}.pdf`}), {...dependencies, files: localFiles})));
    assert.equal(new Set(results.map(result => result.id)).size, 1);
    assert.equal(f.state.attachments.length, 1);
    assert.equal(f.state.audits.length, 1);
    assert.deepEqual(await new Response((await localFiles.get(results[0].id))!.body).arrayBuffer(), pdf());
    assert.deepEqual(await readdir(join(process.cwd(), ".data", "files")), [results[0].id]);
  });
});
