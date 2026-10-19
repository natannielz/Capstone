import type { components } from "../../api/generated/schema";
import { validReference } from "../organizations/access-scope";
import { AccessFailure, accessMessage, stableContent } from "../administration/workflow";
import { IdentityFailure, identityMessage } from "./identity-workflow";
import type { OperationReference, PersonRecoveryReceipt } from "../administration/operation-journal";
import { dateTimeFormatter } from "@/lib/format";

type Schemas = components["schemas"];
export type PersonReviewAccount = Schemas["PersonReviewAccountDto"];
export type PersonReviewTarget = Schemas["PersonReviewTargetDto"];
export type PersonReviewTargetPage = Schemas["PersonReviewTargetPageDto"];
export type PersonReviewResult = Schemas["PersonReviewResultDto"];
export type PersonReviewSummary = Schemas["PersonReviewSummaryDto"];
export type PersonReviewPage = Schemas["PersonReviewPageDto"];
export type PersonReviewDetail = Schemas["PersonReviewDetailDto"];
export type PersonReviewPreview = Schemas["PersonReviewPreviewDto"];
export type PersonReviewAction = "propose" | "approve" | "reject" | "withdraw";
export type PersonReviewDraft = Readonly<{
  actorId: string; action: PersonReviewAction; groupId: string; groupVersion: string;
  proposalId?: string; proposalVersion?: string; reason: string;
}>;
export type PersonReviewCommand = PersonReviewDraft & Readonly<{ previewReference?: string }>;
export const personReviewPath = "/administration/person-reviews";
export const personReviewOperations = {
  propose: "person.review.propose", approve: "person.review.approve", reject: "person.review.reject", withdraw: "person.review.withdraw",
} as const;
const expectedStatus = { propose: "pending", approve: "approved", reject: "rejected", withdraw: "withdrawn" } as const;
const statuses = ["pending", "approved", "rejected", "withdrawn"];
const decisions = ["approve", "reject", "withdraw"];
const invalid = (): never => { throw new AccessFailure(502, "person_response_invalid"); };
const record = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === "object" && !Array.isArray(value);
const id = (value: unknown): value is string => typeof value === "string" && validReference(value);
const count = (value: unknown): value is string => value === "0" || id(value);
const text = (value: unknown, max: number): value is string => typeof value === "string" && value.trim().length > 0 && value.length <= max;
const time = (value: unknown): value is string => typeof value === "string" && value.length <= 40 && /^\d{4}-\d\d-\d\dT/.test(value) && Number.isFinite(Date.parse(value));
const previewReference = (value: unknown): value is string => typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(value);

/** Keep safe presentation fields only; unexpected source evidence must never reach component state. */
export function parsePersonReviewAccount(value: unknown): PersonReviewAccount {
  if (!record(value) || !id(value.userId) || !(value.displayName === null || text(value.displayName, 200)) || !text(value.verifiedEmail, 320) ||
    typeof value.status !== "string" || !["pending", "active", "suspended", "revoked"].includes(value.status)) return invalid();
  return { userId: value.userId, displayName: value.displayName, verifiedEmail: value.verifiedEmail, status: value.status };
}
function accounts(value: unknown): PersonReviewAccount[] {
  if (!Array.isArray(value) || value.length < 1 || value.length > 256) return invalid();
  const result = value.map(parsePersonReviewAccount);
  if (new Set(result.map(item => item.userId)).size !== result.length) return invalid();
  return result;
}
export function parsePersonReviewResult(value: unknown): PersonReviewResult {
  if (!record(value) || !id(value.proposalId) || !id(value.version) || !id(value.groupId) || !id(value.groupVersion) ||
    typeof value.status !== "string" || !statuses.includes(value.status) || !time(value.recordedAt)) return invalid();
  return { proposalId: value.proposalId, version: value.version, status: value.status, groupId: value.groupId, groupVersion: value.groupVersion, recordedAt: value.recordedAt };
}
export function parsePersonReviewTarget(value: unknown): PersonReviewTarget {
  if (!record(value) || !id(value.groupId) || !id(value.version) || typeof value.status !== "string" || !["active", "disputed"].includes(value.status)) return invalid();
  return { groupId: value.groupId, version: value.version, status: value.status, accounts: accounts(value.accounts) };
}
export function parsePersonReviewSummary(value: unknown): PersonReviewSummary {
  if (!record(value) || !text(value.reason, 1000) || !id(value.affectedAccountCount) || BigInt(value.affectedAccountCount) > BigInt(256) ||
    !Array.isArray(value.allowedActions) || value.allowedActions.some(action => typeof action !== "string" || !decisions.includes(action)) ||
    new Set(value.allowedActions).size !== value.allowedActions.length) return invalid();
  const result = parsePersonReviewResult(value.result);
  if (result.status !== "pending" && value.allowedActions.length !== 0) return invalid();
  return { result, requester: parsePersonReviewAccount(value.requester), reason: value.reason, affectedAccountCount: value.affectedAccountCount, allowedActions: [...value.allowedActions] as string[] };
}
export function parsePersonReviewPage(value: unknown): PersonReviewPage {
  if (!record(value) || !count(value.total) || !Array.isArray(value.items) || value.items.length > 25 || BigInt(value.total) < BigInt(value.items.length)) return invalid();
  const items = value.items.map(parsePersonReviewSummary);
  if (new Set(items.map(item => item.result.proposalId)).size !== items.length) return invalid();
  return { total: value.total, items };
}
export function parsePersonReviewTargets(value: unknown): PersonReviewTargetPage {
  if (!record(value) || !count(value.total) || !Array.isArray(value.items) || value.items.length > 25 || BigInt(value.total) < BigInt(value.items.length)) return invalid();
  const items = value.items.map(parsePersonReviewTarget);
  if (new Set(items.map(item => item.groupId)).size !== items.length) return invalid();
  return { total: value.total, items };
}
export function parsePersonReviewDetail(value: unknown): PersonReviewDetail {
  if (!record(value)) return invalid();
  const summary = parsePersonReviewSummary(value.summary), affected = accounts(value.accounts);
  if (summary.affectedAccountCount !== String(affected.length)) return invalid();
  let decision: PersonReviewDetail["decision"] = null;
  if (value.decision !== null) {
    if (!record(value.decision) || !text(value.decision.reason, 1000) || !time(value.decision.decidedAt)) return invalid();
    decision = { actor: parsePersonReviewAccount(value.decision.actor), reason: value.decision.reason, decidedAt: value.decision.decidedAt };
  }
  if ((summary.result.status === "pending") !== (decision === null)) return invalid();
  return { summary, accounts: affected, decision };
}
export function parsePersonReviewPreview(value: unknown): PersonReviewPreview {
  if (!record(value) || !previewReference(value.reference) || !time(value.expiresAt) || (value.kind !== "propose" && value.kind !== "approve")) return invalid();
  const target = parsePersonReviewTarget(value.target), proposal = value.proposal === null ? null : parsePersonReviewSummary(value.proposal);
  if (value.kind === "propose" ? proposal !== null : proposal === null || proposal.result.status !== "pending" || proposal.result.groupId !== target.groupId ||
    proposal.result.groupVersion !== target.version || proposal.affectedAccountCount !== String(target.accounts.length)) return invalid();
  return { reference: value.reference, kind: value.kind, expiresAt: value.expiresAt, target, proposal };
}

export function personReviewPermission(action: PersonReviewAction) { return action === "approve" || action === "reject" ? "person.review.approve" : "person.review.propose"; }
export function personReviewActionAllowed(action: Exclude<PersonReviewAction, "propose">, summary: PersonReviewSummary, permissions: readonly string[]) {
  return summary.result.status === "pending" && summary.allowedActions.includes(action) && permissions.includes(personReviewPermission(action));
}
export function personReviewCommand(draft: PersonReviewDraft, preview: PersonReviewPreview | null, now = Date.now()): PersonReviewCommand {
  if (!id(draft.actorId) || !id(draft.groupId) || !id(draft.groupVersion) || !text(draft.reason.trim(), 1000) || !Object.hasOwn(personReviewOperations, draft.action) ||
    (draft.action === "propose" ? draft.proposalId !== undefined || draft.proposalVersion !== undefined : !id(draft.proposalId) || !id(draft.proposalVersion)))
    throw new AccessFailure(400, "person_review_invalid");
  if (draft.action === "propose" || draft.action === "approve") {
    if (!preview || preview.kind !== draft.action || !previewReference(preview.reference) || !time(preview.expiresAt) || !Number.isFinite(now) || Date.parse(preview.expiresAt) <= now ||
      preview.target.groupId !== draft.groupId || preview.target.version !== draft.groupVersion ||
      (draft.action === "approve" && (preview.proposal?.result.proposalId !== draft.proposalId || preview.proposal?.result.version !== draft.proposalVersion)))
      throw new AccessFailure(409, "person_preview_stale");
  }
  return Object.freeze({ actorId: draft.actorId, action: draft.action, groupId: draft.groupId, groupVersion: draft.groupVersion,
    ...(draft.proposalId ? { proposalId: draft.proposalId, proposalVersion: draft.proposalVersion } : {}), reason: draft.reason.trim(),
    ...(draft.action === "propose" || draft.action === "approve" ? { previewReference: preview!.reference } : {}) });
}

/** A response can only satisfy the currently displayed actor, selection, reason and preview attempt. */
export class PersonReviewPreviewSession {
  private generation = 0;
  private content = "";
  private accepted: PersonReviewPreview | null = null;
  begin(draft: PersonReviewDraft) { this.accepted = null; this.content = stableContent(draft); return { generation: ++this.generation, content: this.content }; }
  accept(ticket: { generation: number; content: string }, draft: PersonReviewDraft, preview: PersonReviewPreview, now = Date.now()) {
    if (ticket.generation !== this.generation || ticket.content !== this.content || stableContent(draft) !== this.content) return false;
    personReviewCommand(draft, preview, now);
    this.accepted = parsePersonReviewPreview(preview); return true;
  }
  invalidate() { this.generation++; this.content = ""; this.accepted = null; }
  get value() { return this.accepted; }
}

/** Abort replaced reads and independently reject late results from transports that ignore abort. */
export class LatestPersonReviewRead {
  private controller: AbortController | null = null;
  begin() {
    this.cancel();
    const controller = new AbortController(); this.controller = controller;
    return { signal: controller.signal, current: () => this.controller === controller && !controller.signal.aborted };
  }
  cancel() { this.controller?.abort(); this.controller = null; }
}

export function validatePersonReviewOutcome(value: unknown, action: PersonReviewAction, expected?: Pick<PersonReviewCommand, "proposalId" | "groupId">): PersonReviewResult {
  const result = parsePersonReviewResult(value);
  if (result.status !== expectedStatus[action] || (expected?.proposalId !== undefined && result.proposalId !== expected.proposalId) ||
    (expected?.groupId !== undefined && result.groupId !== expected.groupId)) return invalid();
  return result;
}
export function personReviewReceipt(value: unknown, entry: OperationReference, expected?: Pick<PersonReviewCommand, "proposalId" | "groupId">): PersonRecoveryReceipt | null {
  const action = (Object.keys(personReviewOperations) as PersonReviewAction[]).find(item => personReviewOperations[item] === entry.operation);
  if (!action || !record(value) || Object.keys(value).sort().join(",") !== "recordedAt,result,state") return invalid();
  if (value.state === "not_confirmed") { if (value.result !== null || value.recordedAt !== null) return invalid(); return null; }
  if (!time(value.recordedAt)) return invalid();
  if (value.state === "closed_no_effect") { if (value.result !== null) return invalid(); return { state: "closed", result: null, recordedAt: value.recordedAt }; }
  if (value.state !== "committed") return invalid();
  const result = validatePersonReviewOutcome(value.result, action, expected);
  if (Date.parse(value.recordedAt) !== Date.parse(result.recordedAt)) return invalid();
  return { state: "committed", result: { id: result.proposalId, version: result.version }, recordedAt: value.recordedAt };
}

export function personReviewNeedsRefresh(error: unknown) {
  return error instanceof AccessFailure && error.code !== "actor_changed" && (error.status === 409 || ["person_preview_stale", "person_preview_expired", "person_preview_invalid", "person_policy_changed", "version_conflict"].includes(error.code));
}

export function personReviewMessage(error: unknown): string {
  if (error instanceof IdentityFailure) return identityMessage(error);
  if (error instanceof AccessFailure) {
    if (["person_governance_unavailable", "person_selection_unavailable"].includes(error.code))
      return "Peninjauan identitas petugas belum diaktifkan. Masuk atau verifikasi dua langkah tidak mengaktifkan layanan ini. Hubungi pengelola yang ditunjuk.";
    if (error.code === "person_profile_unavailable") return "Data akun yang diizinkan untuk ditinjau belum tersedia atau perlu diperbaiki. Hubungi pengelola untuk memeriksa kelengkapan data akun, lalu muat ulang peninjauan.";
    if (error.code === "person_response_invalid") return "Respons belum dapat dipastikan. Data peninjauan tidak ditampilkan; simpan referensi pemulihan jika permintaan perubahan sudah dikirim.";
    if (personReviewNeedsRefresh(error))
      return "Data, izin, atau masa pratinjau telah berubah. Muat data terbaru dan periksa pratinjau lagi sebelum mengajukan.";
    if (error.code === "person_review_invalid") return "Pilih pengajuan atau keterkaitan akun dari hasil yang tersedia, lalu isi alasan hingga 1.000 karakter.";
    if (error.status === 403 && error.code !== "step_up_required") return "Anda belum memiliki izin peninjauan ini dalam lingkup yang disetujui. Hubungi pengelola organisasi Anda.";
  }
  return accessMessage(error);
}
export const personReviewStatus = (value: string) => ({ pending: "Menunggu keputusan", approved: "Penangguhan disetujui", rejected: "Ditolak", withdrawn: "Ditarik", active: "Aktif", disputed: "Perlu peninjauan", suspended: "Ditangguhkan", revoked: "Dicabut" } as Record<string, string>)[value] ?? "Status belum dikenal";
export const personReviewDate = (value: string) => dateTimeFormatter.format(new Date(value));