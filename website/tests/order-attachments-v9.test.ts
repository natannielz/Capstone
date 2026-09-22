import {test} from "node:test";
import assert from "node:assert/strict";
import type {Actor} from "../lib/domain/accounts";
import {DomainError, type Command} from "../lib/domain/model";
import {runCommand} from "../lib/domain/engine";
import {seedState} from "../lib/domain/seed";
import {attachmentContext, canReadAttachment, scopeState, today} from "../lib/domain/selectors";
import {saveAttachment, type AttachmentUpload} from "../lib/server/attachment-upload";

const pdf = (content = "Desain merchandise simulasi") => new TextEncoder().encode(`%PDF-1.7\n${content}`).buffer;

function fixture() {
  const f = {state: seedState(), objects: new Map<string, ArrayBuffer>(), gets: 0, puts: 0, executions: 0,
    afterPut: undefined as (() => void) | undefined, beforeExecute: undefined as (() => void) | undefined};
  f.state.users.push({id: "customer-b", name: "Pelanggan B", role: "customer", divisionId: null, active: true});
  const actor = (id: string) => f.state.users.find(user => user.id === id)!;
  function act(id: string, type: string, data: Record<string, unknown>) {
    const outcome = runCommand(f.state, actor(id), {id: crypto.randomUUID(), type, date: today(), data});
    f.state = outcome.state; return outcome.result.id;
  }
  const orderIds = Object.fromEntries(["customer-demo", "customer-b", "pic-a", "pic-b"].map(id => [id, act(id, "order.create", {neededAt: today(), recipientName: "Penerima", recipientPhone: "0800000000", address: "Alamat simulasi", lines: [{productId: "kopi", qty: 1, unitPrice: 28500}]})]));
  const dependencies = {
    files: {
      async get(id: string) {f.gets++; const bytes = f.objects.get(id); return bytes ? {body: new Uint8Array(bytes)} : null;},
      async put(id: string, content: ArrayBuffer) {f.puts++; if (f.objects.has(id)) throw new Error("Immutable object"); f.objects.set(id, content.slice(0)); f.afterPut?.();},
    },
    async loadState() {return structuredClone(f.state);},
    async execute(user: Actor, command: Command) {f.executions++; f.beforeExecute?.(); const outcome = runCommand(f.state, actor(user.id), command); f.state = outcome.state; return outcome.result;},
  };
  const upload = (owner = "customer-demo", patch: Partial<AttachmentUpload> = {}): AttachmentUpload => ({scope: "order", targetId: orderIds[owner], name: "desain-simulasi.pdf", content: pdf(), ...patch});
  return {f, actor, act, orderIds, dependencies, upload};
}

test("v9 order design uploads allow the owner/PIC division/Kepala and reject other writers before storage", async () => {
  const {f, actor, dependencies, upload, orderIds} = fixture();
  for (const id of ["customer-b", "pic-a", "pic-b", "staf", "kurir", "laporan", "penagihan", "pimpinan", "akuntansi", "admin"]) {
    await assert.rejects(saveAttachment(actor(id), upload(), dependencies), error => error instanceof DomainError && error.status === 403, id);
  }
  assert.equal(f.gets, 0); assert.equal(f.puts, 0); assert.equal(f.executions, 0);
  const customer = await saveAttachment(actor("customer-demo"), upload(), dependencies);
  const pic = await saveAttachment(actor("pic-a"), upload("pic-a"), dependencies);
  await saveAttachment(actor("kepala"), upload("pic-b"), dependencies);
  const meta = f.state.attachments.find(item => item.id === customer.id)!;
  assert.equal(meta.customerId, "customer-demo"); assert.equal(meta.divisionId, null);
  assert.equal(meta.targetId, orderIds["customer-demo"]); assert.equal(meta.shipmentId, null);
  assert.equal(f.state.attachments.find(item => item.id === pic.id)?.divisionId, "div-ops");
  await assert.rejects(saveAttachment(actor("pic-a"), upload("pic-b"), dependencies), /divisi lain/);
  await assert.rejects(saveAttachment(actor("customer-demo"), upload("customer-b"), dependencies), /pembeli lain/);
});

test("v9 order attachment reads and scoped metadata are isolated; couriers/admin/finance cannot obtain design documents", async () => {
  const {f, actor, dependencies, upload} = fixture();
  const customer = await saveAttachment(actor("customer-demo"), upload(), dependencies);
  const division = await saveAttachment(actor("pic-a"), upload("pic-a"), dependencies);
  const customerReaders = new Set(["customer-demo", "kepala", "staf", "laporan"]);
  const divisionReaders = new Set(["pic-a", "kepala", "staf", "laporan"]);
  for (const user of f.state.users) {
    assert.equal(canReadAttachment(f.state, user, customer.id), customerReaders.has(user.id), `${user.id} customer read`);
    assert.equal(canReadAttachment(f.state, user, division.id), divisionReaders.has(user.id), `${user.id} division read`);
    const scoped = scopeState(f.state, user);
    assert.equal(scoped.attachments.some(item => item.id === customer.id), customerReaders.has(user.id), `${user.id} customer metadata`);
    assert.equal(scoped.attachments.some(item => item.id === division.id), divisionReaders.has(user.id), `${user.id} division metadata`);
  }
  const item = f.state.attachments.find(attachment => attachment.id === customer.id)!;
  item.targetId = "missing-order";
  assert.equal(canReadAttachment(f.state, actor("kepala"), item.id), false);
  item.targetId = f.state.orders.find(order => order.customerId === "customer-b")!.id;
  assert.equal(canReadAttachment(f.state, actor("customer-demo"), item.id), false, "forged matching metadata cannot override the target order buyer");
});

test("v9 reviewed, rejected, and cancelled orders retain read-only design history and deny new bytes", async () => {
  for (const status of ["approved", "rejected", "cancelled"] as const) {
    const {f, actor, act, dependencies, upload, orderIds} = fixture();
    const existing = await saveAttachment(actor("customer-demo"), upload(), dependencies);
    if (status === "cancelled") act("customer-demo", "order.cancel", {id: orderIds["customer-demo"], reason: "Kebutuhan dibatalkan"});
    else act("kepala", "order.review", {id: orderIds["customer-demo"], approve: status === "approved", reason: "Spesifikasi tidak tersedia"});
    const before = structuredClone(f.state), puts = f.puts, gets = f.gets;
    await assert.rejects(saveAttachment(actor("customer-demo"), upload("customer-demo", {content: pdf("Ubah desain")}), dependencies), error => error instanceof DomainError && error.status === 409, status);
    assert.equal(f.puts, puts); assert.equal(f.gets, gets); assert.deepEqual(f.state, before);
    assert.equal(canReadAttachment(f.state, actor("customer-demo"), existing.id), true);
    assert.equal(canReadAttachment(f.state, actor("staf"), existing.id), true);
    assert.throws(() => attachmentContext(f.state, actor("kepala"), "order", orderIds["customer-demo"]), /hanya dapat dibaca/);
  }
});

test("v9 order review races are checked after blob write and again in guarded metadata commit", async () => {
  for (const phase of ["after-put", "during-commit"] as const) {
    const {f, actor, dependencies, upload, orderIds} = fixture();
    const review = () => {f.state.orders.find(order => order.id === orderIds["customer-demo"])!.status = "approved";};
    if (phase === "after-put") f.afterPut = review; else f.beforeExecute = review;
    await assert.rejects(saveAttachment(actor("customer-demo"), upload(), dependencies), /hanya dapat dibaca/, phase);
    assert.equal(f.state.attachments.length, 0);
    assert.equal(f.puts, 1);
    assert.equal(f.executions, phase === "after-put" ? 0 : 1);
    assert.equal(f.objects.size, 1, "unreferenced immutable object is not published through attachment metadata");
  }
});

test("v9 identical order design retries retain one record and first filename; closed period snapshots stay frozen", async () => {
  const {f, actor, dependencies, upload} = fixture();
  const period = f.state.periods.find(period => period.id === today().slice(0, 7))!;
  Object.assign(period, {status: "closed", approvedRevision: period.revision, snapshot: {unchanged: true}, closedAt: new Date().toISOString()});
  const before = structuredClone(period);
  const first = await saveAttachment(actor("customer-demo"), upload(), dependencies);
  const retry = await saveAttachment(actor("customer-demo"), upload("customer-demo", {name: "renamed.pdf"}), dependencies);
  assert.deepEqual(first, retry); assert.equal(f.puts, 1); assert.equal(f.state.attachments.length, 1);
  assert.equal(f.state.attachments[0].name, "desain-simulasi.pdf");
  assert.equal(f.state.audits.filter(audit => audit.action === "attachment.add").length, 1);
  assert.deepEqual(f.state.periods.find(period => period.id === before.id), before);
  assert.ok(f.state.audits.at(-1)!.date.slice(0, 7) > before.id);
});

test("v9 order specifications enforce PNG/JPG/PDF signature and 4 MB bound before object access", async () => {
  const {f, actor, dependencies, upload} = fixture();
  for (const content of [new ArrayBuffer(0), new ArrayBuffer(4 * 1024 * 1024 + 1), new TextEncoder().encode("not a design PDF").buffer]) await assert.rejects(saveAttachment(actor("customer-demo"), upload("customer-demo", {content}), dependencies), DomainError);
  assert.equal(f.gets, 0); assert.equal(f.puts, 0);
  const contents = [pdf(), new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]).buffer, new Uint8Array([255, 216, 255, 1]).buffer];
  for (const content of contents) await saveAttachment(actor("customer-demo"), upload("customer-demo", {content}), dependencies);
  assert.deepEqual(f.state.attachments.map(item => item.mime), ["application/pdf", "image/png", "image/jpeg"]);
});
