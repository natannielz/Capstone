import { AccessFailure, MutationLedger, stableContent, businessRefusal } from "../administration/workflow";
import { OperationJournal, submitTrackedRequest, type OperationReference, type StockSourceRevisionRecoveryReceipt } from "../administration/operation-journal";
import { IdentityFailure, sessionTiming, type IdentitySession } from "../identity/identity-workflow";
import type { AccessScope } from "../organizations/access-scope";
import { stockPageRequest, type StockWarehouse } from "./stock-count-workflow";
import type { StockSourceGateway } from "./stock-source-client";
import type { StockSourceRevisionGateway } from "./stock-source-revision-client";
import { sourceActor, sourceAgeSeconds, sourceLocationKey, type SourceDecision, type SourcePurpose } from "./stock-source-workflow";
import { parseCurrentSource, parseSourceRevision, prepareSourceRevision, prepareSourceRevisionDecision, sourceRevisionOperation,
  sourceRevisionOutcome, sourceRevisionReceipt, type CurrentSource, type SourceRevision, type SourceRevisionCommand } from "./stock-source-revision-workflow";

export type RevisionDraft = { label: string; mode: string; control: string; maximumAgeSeconds: string; safetyBufferBaseUnits: string;
  externalNamespace: string; reservationProtocolReference: string; discrepancyOwnerReference: string; handoffReference: string; reason: string };
type Page<T> = { total: string; items: T[] };
type ReadKind = "warehouses" | "sources" | "current" | "history" | "detail";
type Result = ReturnType<typeof sourceRevisionOutcome>;
export type RevisionEditorSnapshot = Readonly<{
  purpose: SourcePurpose; warehouses: StockWarehouse[]; warehouse: StockWarehouse | null; sources: Page<CurrentSource>; sourceOffset: number;
  current: CurrentSource | null; history: Page<SourceRevision>; historyOffset: number; selected: SourceRevision | null; draft: RevisionDraft;
  review: SourceRevisionCommand | null; attempt: SourceRevisionCommand | null; result: Result | null; references: OperationReference[];
  loadingWarehouses: boolean; loadingSources: boolean; loadingCurrent: boolean; loadingHistory: boolean; loadingDetail: boolean;
  sending: boolean; recovering: boolean; expired: boolean; suspended: boolean; fresh: boolean; error: unknown; recoveryError: unknown;
}>;
type Options = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number; now: () => number;
  gateway: StockSourceRevisionGateway; warehouses: StockSourceGateway["warehouses"]; journal: OperationJournal; newKey?: () => string };
const empty = <T>(): Page<T> => ({ total: "0", items: [] });
const blank = (): RevisionDraft => ({ label: "", mode: "", control: "", maximumAgeSeconds: "", safetyBufferBaseUnits: "",
  externalNamespace: "", reservationProtocolReference: "", discrepancyOwnerReference: "", handoffReference: "", reason: "" });
function draftOf(current: CurrentSource): RevisionDraft {
  const s = current.settings;
  return { label: s.label, mode: s.mode, control: s.control, maximumAgeSeconds: sourceAgeSeconds(s.maximumAgeMilliseconds),
    safetyBufferBaseUnits: s.safetyBufferBaseUnits, externalNamespace: s.externalNamespace ?? "", reservationProtocolReference: s.reservationProtocolReference ?? "",
    discrepancyOwnerReference: s.discrepancyOwnerReference, handoffReference: s.handoffReference, reason: "" };
}
function milliseconds(value: string) {
  const match = /^(0|[1-9][0-9]{0,18})(?:\.([0-9]{1,3}))?$(?![\s\S])/.exec(value);
  if (!match) throw new AccessFailure(400, "stock_source_revision_input_invalid");
  return (BigInt(match[1]) * BigInt(1000) + BigInt((match[2] ?? "").padEnd(3, "0"))).toString();
}

/** A mounted account owns its reads, immutable reviewed command and original uncertain request reference. */
export class StockSourceRevisionEditor {
  readonly canRead: boolean; readonly canPropose: boolean; readonly canApprove: boolean; readonly journal: OperationJournal;
  private state: RevisionEditorSnapshot; private active = false; private generation = 0;
  private readonly reads = new Map<ReadKind, AbortController>(); private readonly listeners = new Set<() => void>();
  private readonly session: IdentitySession; private readonly ledger: MutationLedger;
  private activeReference: { operation: string; key: string } | null = null;
  constructor(private readonly options: Options) {
    sourceActor(options.actorId);
    if (!options.session.authenticated || options.session.account?.userId !== options.actorId) throw new AccessFailure(409, "actor_changed");
    this.canRead = options.actions.includes("stock.source.read"); this.canPropose = options.actions.includes("stock.source.propose");
    this.canApprove = options.actions.includes("stock.source.approve");
    if (!this.canRead && !this.canPropose && !this.canApprove) throw new AccessFailure(403, "access_denied");
    this.session = structuredClone(options.session); this.journal = options.journal; this.ledger = new MutationLedger(options.newKey);
    this.state = { purpose: this.canRead ? "inventory-source-history" : this.canPropose ? "inventory-source-propose" : "inventory-source-review",
      warehouses: [], warehouse: null, sources: empty(), sourceOffset: 0, current: null, history: empty(), historyOffset: 0, selected: null,
      draft: blank(), review: null, attempt: null, result: null, references: [], loadingWarehouses: false, loadingSources: false,
      loadingCurrent: false, loadingHistory: false, loadingDetail: false, sending: false, recovering: false, expired: false, suspended: false,
      fresh: sessionTiming(this.session, options.now() - options.observedAt).fresh, error: null, recoveryError: null };
  }
  snapshot = (): RevisionEditorSnapshot => this.state;
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  private patch(change: Partial<RevisionEditorSnapshot>) { this.state = { ...this.state, ...change }; this.listeners.forEach(listener => listener()); }
  isCurrent = () => this.active && !this.state.expired && !this.state.suspended && !sessionTiming(this.session, this.options.now() - this.options.observedAt).expired;
  private guard() { this.checkSession(); return this.isCurrent(); }
  get selectionLocked() { return !this.isCurrent() || this.state.sending || this.state.recovering || this.state.review !== null || this.state.attempt !== null; }
  get locked() { return this.selectionLocked || this.state.references.length > 0 || this.state.recoveryError !== null; }
  get staleBase() { return !!this.state.selected && !!this.state.current && (this.state.selected.basePoolVersion !== this.state.current.poolVersion ||
    stableContent(this.state.selected.baseSettings) !== stableContent(this.state.current.settings)); }
  private mayEdit() { return this.guard() && !this.locked; }
  private cancel(kind: ReadKind) { this.reads.get(kind)?.abort(); this.reads.delete(kind); }
  private cancelAll() { for (const kind of this.reads.keys()) this.cancel(kind); }
  private begin(kind: ReadKind) {
    this.cancel(kind); const controller = new AbortController(); this.reads.set(kind, controller);
    return { signal: controller.signal, current: () => this.isCurrent() && this.reads.get(kind) === controller && !controller.signal.aborted };
  }
  private cleared() {
    return { current: null, history: empty<SourceRevision>(), historyOffset: 0, selected: null, draft: blank(), review: null, result: null,
      loadingCurrent: false, loadingHistory: false, loadingDetail: false };
  }
  private conceal(change: Partial<RevisionEditorSnapshot>) {
    this.generation++; this.cancelAll();
    this.patch({ ...this.cleared(), warehouses: [], warehouse: null, sources: empty(), loadingWarehouses: false, loadingSources: false, attempt: null, ...change });
  }
  checkSession() {
    const timing = sessionTiming(this.session, this.options.now() - this.options.observedAt);
    if (this.active && !this.state.expired && timing.expired) this.conceal({ expired: true, fresh: false, error: new IdentityFailure(401, "session_expired") });
    else if (this.active && this.state.fresh !== timing.fresh) this.patch({ fresh: timing.fresh });
  }
  dispose() { this.active = false; this.conceal({}); }
  async start() { if (this.active) return; this.active = true; this.refreshReferences(); if (this.guard()) await this.loadWarehouses(); }
  refreshReferences = () => {
    try { this.patch({ references: this.journal.list(this.options.actorId), recoveryError: null }); }
    catch (error) { this.patch({ recoveryError: error }); }
  };
  private failed(error: unknown) {
    if (!this.active) return;
    this.patch({ error });
    if (error instanceof AccessFailure || error instanceof IdentityFailure) {
      if (error.code === "step_up_required") this.patch({ fresh: false });
      else if (error.status === 401 || error.status === 403 && !businessRefusal(error.code) || error.code === "actor_changed" ||
        error.code === "stock_source_revision_response_invalid" || error.code === "stock_source_response_invalid") this.conceal({ suspended: true, error });
    }
  }
  async changePurpose(purpose: SourcePurpose) {
    if (!this.guard() || this.selectionLocked || purpose === this.state.purpose) return;
    if (purpose === "inventory-source-history" ? !this.canRead : purpose === "inventory-source-propose" ? !this.canPropose
      : purpose === "inventory-source-review" ? !this.canApprove : true) { this.failed(new AccessFailure(403, "access_denied")); return; }
    this.patch({ purpose }); await this.loadWarehouses();
  }
  async loadWarehouses() {
    if (!this.guard() || this.selectionLocked) return;
    this.generation++; this.cancelAll();
    this.patch({ ...this.cleared(), warehouse: null, sources: empty(), warehouses: [], loadingSources: false, loadingWarehouses: true, error: null });
    const ticket = this.begin("warehouses");
    try { const warehouses = await this.options.warehouses(this.options.actorId, this.state.purpose, ticket.signal);
      if (ticket.current()) this.patch({ warehouses }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingWarehouses: false }); this.checkSession(); }
  }
  async chooseWarehouse(location: AccessScope | null) {
    if (!this.guard() || this.selectionLocked || this.state.loadingWarehouses) return;
    const warehouse = location === null ? null : this.state.warehouses.find(item => sourceLocationKey(item.location) === sourceLocationKey(location));
    if (location !== null && !warehouse) { this.failed(new AccessFailure(400, "stock_source_selection_required")); return; }
    this.generation++; this.cancelAll(); this.patch({ ...this.cleared(), warehouse: warehouse ?? null, sources: empty(), sourceOffset: 0, loadingSources: false, error: null });
    if (warehouse && this.canRead && !this.state.references.length && !this.state.recoveryError) await this.loadSources();
  }
  async loadSources(offset = 0) {
    if (!this.guard() || this.selectionLocked || !this.canRead || !this.state.warehouse || this.state.references.length || this.state.recoveryError) return;
    try { stockPageRequest(offset, 25); } catch (error) { this.failed(error); return; }
    this.generation++; this.cancelAll(); const location = this.state.warehouse.location, ticket = this.begin("sources");
    this.patch({ ...this.cleared(), sources: empty(), sourceOffset: offset, loadingSources: true, error: null });
    try { const sources = await this.options.gateway.sources(this.options.actorId, location, offset, ticket.signal); if (ticket.current()) this.patch({ sources }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingSources: false }); this.checkSession(); }
  }
  async chooseSource(id: string) {
    if (!this.mayEdit() || this.state.loadingSources || !this.canRead || !this.state.warehouse || !this.state.sources.items.some(item => item.physicalPoolId === id)) return;
    this.generation++; this.cancel("current"); this.cancel("history"); this.cancel("detail");
    const location = this.state.warehouse.location, ticket = this.begin("current");
    this.patch({ ...this.cleared(), loadingCurrent: true, error: null });
    try {
      const current = parseCurrentSource(await this.options.gateway.current(this.options.actorId, location, id, ticket.signal));
      if (!ticket.current()) return;
      if (current.physicalPoolId !== id || sourceLocationKey(current.settings.location) !== sourceLocationKey(location)) throw new AccessFailure(502, "stock_source_revision_response_invalid");
      this.patch({ current, draft: draftOf(current), loadingCurrent: false }); await this.loadHistory();
    } catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingCurrent: false }); this.checkSession(); }
  }
  async loadHistory(offset = 0) {
    if (!this.mayEdit() || !this.canRead || !this.state.current || !this.state.warehouse) return;
    try { stockPageRequest(offset, 25); } catch (error) { this.failed(error); return; }
    this.cancel("detail"); const location = this.state.warehouse.location, poolId = this.state.current.physicalPoolId, ticket = this.begin("history");
    this.patch({ history: empty(), historyOffset: offset, selected: null, loadingDetail: false, loadingHistory: true, error: null });
    try { const history = await this.options.gateway.history(this.options.actorId, location, poolId, offset, ticket.signal); if (ticket.current()) this.patch({ history }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingHistory: false }); this.checkSession(); }
  }
  async chooseProposal(id: string) {
    if (!this.mayEdit() || this.state.loadingHistory || !this.canRead || !this.state.current || !this.state.warehouse ||
      !this.state.history.items.some(item => item.id === id)) return;
    const location = this.state.warehouse.location, poolId = this.state.current.physicalPoolId, ticket = this.begin("detail");
    this.patch({ selected: null, loadingDetail: true, error: null });
    try {
      const raw = await this.options.gateway.detail(this.options.actorId, location, id, ticket.signal);
      if (!ticket.current()) return;
      if (raw === null) throw new AccessFailure(409, "stock_source_revision_changed");
      const selected = parseSourceRevision(raw);
      if (selected.id !== id || selected.physicalPoolId !== poolId || sourceLocationKey(selected.proposedSettings.location) !== sourceLocationKey(location))
        throw new AccessFailure(502, "stock_source_revision_response_invalid");
      this.patch({ selected });
    } catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingDetail: false }); this.checkSession(); }
  }
  changeDraft(field: keyof RevisionDraft, value: string) {
    if (!this.mayEdit() || !this.canRead || !this.canPropose || !this.state.current) return;
    const draft = { ...this.state.draft, [field]: value };
    if (field === "mode") { draft.control = ""; draft.externalNamespace = ""; draft.reservationProtocolReference = ""; }
    this.patch({ draft, error: null });
  }
  reviewProposal() {
    this.refreshReferences(); if (!this.mayEdit()) return;
    try {
      if (!this.canRead || !this.canPropose) throw new AccessFailure(403, "access_denied");
      if (!this.state.current || this.state.loadingCurrent) throw new AccessFailure(400, "stock_source_selection_required");
      const d = this.state.draft;
      this.patch({ review: prepareSourceRevision(this.options.actorId, this.state.current, { ...this.state.current.settings,
        label: d.label.trim(), mode: d.mode, control: d.control, maximumAgeMilliseconds: milliseconds(d.maximumAgeSeconds),
        safetyBufferBaseUnits: d.safetyBufferBaseUnits, externalNamespace: d.mode === "externalLedger" ? d.externalNamespace.trim() : null,
        reservationProtocolReference: d.mode === "externalLedger" ? d.reservationProtocolReference.trim() : null,
        discrepancyOwnerReference: d.discrepancyOwnerReference.trim(), handoffReference: d.handoffReference.trim(), reason: d.reason.trim() }), result: null, error: null });
    } catch (error) { this.failed(error); }
  }
  reviewDecision(decision: SourceDecision, reference: string, reason: string) {
    this.refreshReferences(); if (!this.mayEdit()) return;
    try {
      if (!this.canRead || (decision === "withdraw" ? !this.canPropose : !this.canApprove)) throw new AccessFailure(403, "access_denied");
      if (!this.state.selected || !this.state.current || this.state.loadingDetail || this.state.loadingCurrent) throw new AccessFailure(400, "stock_source_selection_required");
      if (decision !== "withdraw" && !this.state.fresh) throw new AccessFailure(403, "step_up_required");
      if (decision === "approve" && (this.staleBase || this.state.current.poolState !== "inactive")) throw new AccessFailure(409, "stock_source_revision_not_approvable");
      this.patch({ review: prepareSourceRevisionDecision(this.options.actorId, this.state.selected, decision, reference, reason), result: null, error: null });
    } catch (error) { this.failed(error); }
  }
  cancelReview() { if (this.guard() && !this.state.sending && !this.state.recovering) this.patch({ review: null, error: null }); }
  async submitReviewed(confirmed: boolean) {
    if (!confirmed || !this.state.review || !this.guard() || this.state.sending || this.state.recovering || this.state.attempt) return;
    this.refreshReferences(); if (this.state.references.length || this.state.recoveryError) return;
    const command = this.state.review;
    if ((command.operation === "stock.source.revision.approve" || command.operation === "stock.source.revision.reject") && !this.state.fresh) {
      this.failed(new AccessFailure(403, "step_up_required")); return;
    }
    const generation = this.generation, isCurrent = () => this.isCurrent() && generation === this.generation;
    this.cancelAll(); this.patch({ sending: true, review: null, attempt: command, error: null, result: null, loadingHistory: false, loadingDetail: false });
    try {
      const result = await submitTrackedRequest(this.ledger, this.journal, this.options.actorId, "revision", command.operation, command, async key => {
        this.activeReference = { operation: command.operation, key };
        const saved = await this.options.gateway.submit(command, key, isCurrent);
        if (!isCurrent()) throw new AccessFailure(503, "commit_outcome_unknown");
        return sourceRevisionOutcome(saved, command);
      });
      if (isCurrent()) { this.activeReference = null; this.patch({ ...this.cleared(), sources: empty(), attempt: null, result }); }
    } catch (error) {
      if (this.active) {
        const uncertain = this.ledger.isUncertain("revision");
        this.patch({ attempt: uncertain ? command : null, error });
        if (!uncertain) { this.activeReference = null; this.patch({ ...this.cleared(), sources: empty(), error }); }
        this.failed(error);
      }
    } finally { if (this.active) { this.patch({ sending: false }); this.refreshReferences(); this.checkSession(); } }
  }
  setRecovering = (recovering: boolean) => { if (this.active) this.patch({ recovering }); };
  receipt = async (entry: OperationReference): Promise<StockSourceRevisionRecoveryReceipt | null> => {
    const generation = this.generation, isCurrent = () => this.isCurrent() && generation === this.generation && entry.actorId === this.options.actorId;
    if (!isCurrent()) throw new AccessFailure(409, "recovery_request_stale");
    const operation = sourceRevisionOperation(entry.operation);
    if (!this.state.warehouse) throw new AccessFailure(400, "stock_source_selection_required");
    const raw = await this.options.gateway.receipt(entry, this.state.warehouse.location);
    if (!isCurrent()) throw new AccessFailure(409, "recovery_request_stale");
    if (raw === null) return null;
    const result = sourceRevisionReceipt({ state: "committed", result: raw }, operation)!;
    if (this.state.attempt && this.activeReference?.operation === entry.operation && this.activeReference.key === entry.key)
      sourceRevisionOutcome(result, this.state.attempt);
    return { state: "committed", result };
  };
  recoveryFailed = (error: unknown) => { this.failed(error); this.refreshReferences(); };
  reconciled = () => {
    this.refreshReferences(); if (this.state.recoveryError) return;
    const pending = this.activeReference;
    if (!pending || !this.state.references.some(entry => entry.operation === pending.operation && entry.key === pending.key)) {
      this.ledger.resetAfterReconciliation(); this.activeReference = null;
      this.patch({ ...this.cleared(), sources: empty(), attempt: null, error: null });
    }
  };
}
