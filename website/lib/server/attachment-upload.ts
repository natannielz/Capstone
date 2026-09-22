import { createHash } from "node:crypto";
import type { Actor } from "../domain/accounts";
import { DomainError, type Attachment, type Command, type CommandResult, type State } from "../domain/model";
import { attachmentContext } from "../domain/selectors";

const MAX_FILE_BYTES = 4 * 1024 * 1024;
const MAX_REQUEST_BYTES = MAX_FILE_BYTES + 20000;

export type AttachmentUpload = {scope: string; targetId: string; name: string; content: ArrayBuffer};
type UploadDependencies = {
  files: {
    get(id: string): Promise<{body: BodyInit} | null>;
    put(id: string, content: ArrayBuffer, options: {httpMetadata: {contentType: string}}): Promise<void>;
  };
  loadState(): Promise<State>;
  execute(actor: Actor, command: Command): Promise<CommandResult>;
  now?: () => Date;
};

/** Bound the actual stream, including chunked bodies whose length is not supplied. */
export async function readAttachmentUpload(request: Request): Promise<AttachmentUpload> {
  if (Number(request.headers.get("content-length") || 0) > MAX_REQUEST_BYTES) {
    await request.body?.cancel().catch(() => {});
    throw new DomainError("Berkas maksimal 4 MB.", 413);
  }
  const reader = request.body?.getReader();
  if (!reader) throw new DomainError("Pilih berkas.");
  const chunks: Uint8Array[] = [];
  let size = 0;
  try {
    while (true) {
      const next = await reader.read();
      if (next.done) break;
      size += next.value.length;
      if (size > MAX_REQUEST_BYTES) throw new DomainError("Berkas maksimal 4 MB.", 413);
      chunks.push(next.value);
    }
  } finally {
    await reader.cancel().catch(() => {});
    reader.releaseLock();
  }
  const bytes = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
  let form: FormData;
  try {
    form = await new Response(bytes, {headers: {"content-type": request.headers.get("content-type") || ""}}).formData();
  } catch {
    throw new DomainError("Format unggahan tidak valid.");
  }
  const file = form.get("file");
  if (!file || typeof file === "string" || file.size < 1 || file.size > MAX_FILE_BYTES) {
    throw new DomainError("Pilih berkas PNG, JPG, atau PDF maksimal 4 MB.");
  }
  return {scope: String(form.get("scope") || ""), targetId: String(form.get("targetId") || ""), name: file.name, content: await file.arrayBuffer()};
}

function authorizedActor(state: State, actor: Actor, upload: AttachmentUpload) {
  const fresh = state.users.find(user => user.id === actor.id);
  if (!fresh) throw new DomainError("Akun tidak aktif.", 403);
  attachmentContext(state, fresh, upload.scope, upload.targetId);
  return fresh;
}

function fileMime(content: ArrayBuffer) {
  if (content.byteLength < 1 || content.byteLength > MAX_FILE_BYTES) {
    throw new DomainError("Pilih berkas PNG, JPG, atau PDF maksimal 4 MB.");
  }
  const head = new Uint8Array(content).slice(0, 8);
  if (head[0] === 0xff && head[1] === 0xd8 && head[2] === 0xff) return "image/jpeg";
  if (head.join(",") === "137,80,78,71,13,10,26,10") return "image/png";
  if (new TextDecoder().decode(head.slice(0, 5)) === "%PDF-") return "application/pdf";
  throw new DomainError("Isi berkas harus PNG, JPG, atau PDF.");
}

async function stored(files: UploadDependencies["files"], id: string) {
  const file = await files.get(id);
  if (!file) return false;
  // Blob GET opens a network stream. This is only an existence probe, so always
  // cancel it without downloading the document (local storage returns bytes).
  if (file.body instanceof ReadableStream) await file.body.cancel().catch(() => {});
  return true;
}

function existingResult(state: State, id: string, actor: Actor, upload: AttachmentUpload, mime: string) {
  const existing: Attachment | undefined = state.attachments.find(attachment => attachment.id === id);
  if (!existing) return;
  if (existing.ownerId !== actor.id || existing.scope !== upload.scope || existing.targetId !== upload.targetId || existing.mime !== mime || existing.size !== upload.content.byteLength) {
    throw new DomainError("Identitas lampiran tidak sesuai. Hubungi petugas toko.", 409);
  }
  return {id: existing.id, message: "Lampiran tersimpan."};
}

function openDate(state: State, now: Date) {
  const date = now.toLocaleDateString("en-CA", {timeZone: "Asia/Jakarta"});
  const closed = state.periods.filter(period => period.status === "closed").map(period => period.id).sort().at(-1);
  if (!closed || closed < date.slice(0, 7)) return date;
  const [year, month] = closed.split("-").map(Number);
  return new Date(Date.UTC(year, month, 1)).toISOString().slice(0, 10);
}

export async function saveAttachment(actor: Actor, upload: AttachmentUpload, dependencies: UploadDependencies): Promise<CommandResult> {
  const {files, loadState, execute} = dependencies;
  authorizedActor(await loadState(), actor, upload);
  const mime = fileMime(upload.content);
  const contentHash = createHash("sha256").update(Buffer.from(upload.content)).digest("hex");
  // The namespace is versioned and unambiguous; filename/date are deliberately
  // excluded so a renamed retry or a retry after midnight has one identity.
  const id = `attachment-${createHash("sha256").update(JSON.stringify(["v1", actor.id, upload.scope, upload.targetId, contentHash])).digest("hex")}`;
  if (!await stored(files, id)) {
    try {
      await files.put(id, upload.content, {httpMetadata: {contentType: mime}});
    } catch (error) {
      // Private Blob disallows overwriting by default. A competing put (or a
      // put whose response was lost) may already have stored these exact bytes.
      if (!await stored(files, id)) throw error;
    }
  }
  const state = await loadState();
  const fresh = authorizedActor(state, actor, upload);
  const existing = existingResult(state, id, fresh, upload, mime);
  if (existing) return existing;
  const name = upload.name.replace(/[\x00-\x1f\\/]/g, "_").slice(0, 180).trim() || "Bukti simulasi";
  try {
    // The domain reauthorizes attachmentContext inside the guarded transaction.
    return await execute(fresh, {id, type: "attachment.add", date: openDate(state, (dependencies.now || (() => new Date()))()), data: {attachmentId: id, targetId: upload.targetId, scope: upload.scope, name, mime, size: upload.content.byteLength}});
  } catch (error) {
    // Concurrent requests may differ in display name/date and therefore command
    // fingerprint. Also recover a committed transaction with a lost response.
    const latest = await loadState();
    const latestActor = authorizedActor(latest, actor, upload);
    const committed = existingResult(latest, id, latestActor, upload, mime);
    if (committed) return committed;
    // Never delete the canonical object here: an ambiguous/competing commit may
    // reference it. A failed uncommitted upload can leave one bounded (<=4 MB)
    // orphan per actor/target/content, reusable on retry; cleanup requires a
    // separate reconciliation after all in-flight metadata writes have ended.
    throw error;
  }
}
