import { AccessFailure, MutationLedger, businessRefusal } from "../administration/workflow";
import { OperationJournal, submitTrackedRequest, type OperationReference, type StockSourceRecoveryReceipt } from "../administration/operation-journal";
import { IdentityFailure, sessionTiming, type IdentitySession } from "../identity/identity-workflow";
import type { AccessScope } from "../organizations/access-scope";
import type { StockWarehouse } from "./stock-count-workflow";
import { stockPageRequest } from "./stock-count-workflow";
import type { StockSourceGateway } from "./stock-source-client";
import { sourceActor, sourceLocationKey, sourceOperation, prepareSourceProposal, prepareSourceDecision,
  type SourceDraft, type SourceDetail, type SourceCommand, type SourceDecision, type SourceResult, type SourcePurpose } from "./stock-source-workflow";

type Mode = "propose" | "history" | "review";
type ReadKind = "warehouses" | "history" | "detail";
type Page = { total: string; items: SourceDetail[] };
export type SourceEditorSnapshot = Readonly<{
  mode: Mode; warehouses: StockWarehouse[]; warehouse: StockWarehouse | null; history: Page; historyOffset: number;
  selected: SourceDetail | null; draft: SourceDraft; review: SourceCommand | null; attempt: SourceCommand | null; result: SourceResult | null;
  loadingWarehouses: boolean; loadingHistory: boolean; loadingDetail: boolean; sending: boolean; recovering: boolean;
  expired: boolean; suspended: boolean; fresh: boolean; error: unknown; recoveryError: unknown; references: OperationReference[];
}>;
type Options = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number; now: () => number;
  gateway: StockSourceGateway; journal: OperationJournal; newKey?: () => string };
const empty = (): Page => ({ total: "0", items: [] });
const blank = (): SourceDraft => ({ physicalKey: "", label: "", mode: "", control: "", maximumAgeSeconds: "", safetyBufferBaseUnits: "",
  externalNamespace: "", reservationProtocolReference: "", discrepancyOwnerReference: "", handoffReference: "", reason: "" });
const purposes: Record<Mode, SourcePurpose> = { propose: "inventory-source-propose", history: "inventory-source-history", review: "inventory-source-review" };

/** Each mounted account view owns its reads, immutable review and original request key. */
export class StockSourceEditor {
  readonly canPropose: boolean; readonly canRead: boolean; readonly canApprove: boolean; readonly journal: OperationJournal;
  private state: SourceEditorSnapshot;
  private active = false; private generation = 0;
  private readonly reads = new Map<ReadKind, AbortController>();
  private readonly listeners = new Set<() => void>();
  private readonly ledger: MutationLedger;
  private readonly session: IdentitySession;
  private activeReference: { operation: string; key: string } | null = null;
  constructor(private readonly options: Options) {
    sourceActor(options.actorId);
    if (!options.session.authenticated || options.session.account?.userId !== options.actorId) throw new AccessFailure(409, "actor_changed");
    this.canPropose = options.actions.includes("stock.source.propose"); this.canRead = options.actions.includes("stock.source.read");
    this.canApprove = options.actions.includes("stock.source.approve");
    if (!this.canPropose && !this.canRead && !this.canApprove) throw new AccessFailure(403, "access_denied");
    this.session = structuredClone(options.session); this.journal = options.journal; this.ledger = new MutationLedger(options.newKey);
    this.state = { mode: this.canPropose ? "propose" : this.canRead ? "history" : "review", warehouses: [], warehouse: null,
      history: empty(), historyOffset: 0, selected: null, draft: blank(), review: null, attempt: null, result: null,
      loadingWarehouses: false, loadingHistory: false, loadingDetail: false, sending: false, recovering: false,
      expired: false, suspended: false, fresh: sessionTiming(this.session, options.now() - options.observedAt).fresh,
      error: null, recoveryError: null, references: [] };
  }
  snapshot = (): SourceEditorSnapshot => this.state;
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  private patch(change: Partial<SourceEditorSnapshot>) { this.state = { ...this.state, ...change }; this.listeners.forEach(listener => listener()); }
  isCurrent = () => this.active && !this.state.expired && !this.state.suspended && !sessionTiming(this.session, this.options.now() - this.options.observedAt).expired;
  private guard() { this.checkSession(); return this.isCurrent(); }
  // A reloaded reference has no stored location. Allow a deliberate location choice while new writes stay locked.
  get selectionLocked() { return !this.isCurrent() || this.state.sending || this.state.recovering || this.state.review !== null || this.state.attempt !== null; }
  get locked() { return this.selectionLocked || this.state.references.length > 0 || this.state.recoveryError !== null; }
  private mayEdit() { return this.guard() && !this.locked; }
  private cancel(kind: ReadKind) { this.reads.get(kind)?.abort(); this.reads.delete(kind); }
  private cancelAll() { for (const kind of this.reads.keys()) this.cancel(kind); }
  private begin(kind: ReadKind) {
    this.cancel(kind); const controller = new AbortController(); this.reads.set(kind, controller);
    return { signal: controller.signal, current: () => this.isCurrent() && this.reads.get(kind) === controller && !controller.signal.aborted };
  }
  private cleared() { return { warehouse: null, history: empty(), historyOffset: 0, selected: null, draft: blank(), review: null,
    result: null, loadingHistory: false, loadingDetail: false }; }
  private conceal(change: Partial<SourceEditorSnapshot>) {
    this.generation++; this.cancelAll(); this.patch({ ...this.cleared(), warehouses: [], loadingWarehouses: false, ...change });
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
      else if (error.status === 401 || error.status === 403 && !businessRefusal(error.code) || error.code === "actor_changed" || error.code === "stock_source_response_invalid")
        this.conceal({ suspended: true, error });
    }
  }
  async changeMode(mode: Mode) {
    if (!this.guard() || this.selectionLocked || mode === this.state.mode) return;
    if (mode === "propose" ? !this.canPropose : mode === "history" ? !this.canRead : mode === "review" ? !this.canApprove : true) {
      this.patch({ error: new AccessFailure(403, "access_denied") }); return;
    }
    this.patch({ mode }); await this.loadWarehouses();
  }
  async loadWarehouses() {
    if (!this.guard() || this.selectionLocked) return;
    this.cancelAll(); this.patch({ ...this.cleared(), warehouses: [], loadingWarehouses: true, error: null });
    const ticket = this.begin("warehouses");
    try {
      const warehouses = await this.options.gateway.warehouses(this.options.actorId, purposes[this.state.mode], ticket.signal);
      if (ticket.current()) this.patch({ warehouses });
    } catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingWarehouses: false }); this.checkSession(); }
  }
  async chooseWarehouse(location: AccessScope | null) {
    if (!this.guard() || this.selectionLocked || this.state.loadingWarehouses) return;
    const warehouse = location === null ? null : this.state.warehouses.find(item => sourceLocationKey(item.location) === sourceLocationKey(location));
    if (location !== null && !warehouse) { this.patch({ error: new AccessFailure(400, "stock_source_selection_required") }); return; }
    this.cancel("history"); this.cancel("detail"); this.generation++;
    this.patch({ ...this.cleared(), warehouse: warehouse ?? null, error: null });
    if (warehouse && this.canRead && this.state.mode !== "propose") await this.loadHistory();
  }
  async loadHistory(offset = 0) {
    if (!this.guard() || this.selectionLocked || !this.canRead || this.state.mode === "propose" || !this.state.warehouse) return;
    try { stockPageRequest(offset, 25); } catch (error) { this.failed(error); return; }
    this.cancel("detail"); const location = this.state.warehouse.location, ticket = this.begin("history");
    this.patch({ history: empty(), historyOffset: offset, selected: null, loadingDetail: false, loadingHistory: true, error: null });
    try {
      const history = await this.options.gateway.history(this.options.actorId, location, offset, ticket.signal);
      if (ticket.current()) this.patch({ history });
    } catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingHistory: false }); this.checkSession(); }
  }
  async chooseProposal(id: string) {
    if (!this.mayEdit() || this.state.loadingHistory || !this.canRead || !this.state.warehouse || !this.state.history.items.some(item => item.id === id)) return;
    const location = this.state.warehouse.location, ticket = this.begin("detail");
    this.patch({ selected: null, loadingDetail: true, error: null, result: null });
    try {
      const selected = await this.options.gateway.detail(this.options.actorId, location, id, ticket.signal);
      if (ticket.current()) this.patch({ selected, error: selected ? null : new AccessFailure(409, "stock_source_revision_changed") });
    } catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingDetail: false }); this.checkSession(); }
  }
  changeDraft(field: keyof SourceDraft, value: string) {
    if (!this.mayEdit()) return;
    const draft = { ...this.state.draft, [field]: value };
    if (field === "mode") { draft.control = ""; draft.externalNamespace = ""; draft.reservationProtocolReference = ""; }
    this.patch({ draft, error: null });
  }
  reviewProposal() {
    this.refreshReferences(); if (!this.mayEdit()) return;
    try {
      if (!this.canPropose || this.state.mode !== "propose" || !this.state.warehouse) throw new AccessFailure(400, "stock_source_selection_required");
      this.patch({ review: prepareSourceProposal(this.options.actorId, this.state.warehouse.location, this.state.draft), result: null, error: null });
    } catch (error) { this.failed(error); }
  }
  reviewDecision(decision: SourceDecision, reference: string, reason: string) {
    this.refreshReferences(); if (!this.mayEdit()) return;
    try {
      if (!this.canRead || (decision === "withdraw" ? !this.canPropose : !this.canApprove)) throw new AccessFailure(403, "access_denied");
      if (!this.state.selected || this.state.loadingDetail) throw new AccessFailure(400, "stock_source_selection_required");
      if (decision !== "withdraw" && !this.state.fresh) throw new AccessFailure(403, "step_up_required");
      this.patch({ review: prepareSourceDecision(this.options.actorId, this.state.selected, decision, reference, reason), result: null, error: null });
    } catch (error) { this.failed(error); }
  }
  cancelReview() { if (this.guard() && !this.state.sending && !this.state.recovering) this.patch({ review: null, error: null }); }
  async submitReviewed(confirmed: boolean) {
    if (!confirmed || !this.state.review || !this.guard() || this.state.sending || this.state.recovering) return;
    this.refreshReferences(); if (this.state.references.length || this.state.recoveryError) return;
    await this.send(this.state.review);
  }
  async retry() {
    if (!this.state.attempt || !this.guard() || this.state.sending || this.state.recovering || !this.ledger.isUncertain("source")) return;
    await this.send(this.state.attempt);
  }
  private async send(command: SourceCommand) {
    if (command.actorId !== this.options.actorId) return;
    if ((command.operation === "stock.source.approve" || command.operation === "stock.source.reject") && !this.state.fresh) {
      this.failed(new AccessFailure(403, "step_up_required")); return;
    }
    const generation = this.generation, current = () => this.isCurrent() && generation === this.generation;
    this.cancelAll(); this.patch({ sending: true, review: null, attempt: command, error: null, result: null, loadingHistory: false, loadingDetail: false });
    try {
      const result = await submitTrackedRequest(this.ledger, this.journal, this.options.actorId, "source", command.operation, command, async key => {
        this.activeReference = { operation: command.operation, key };
        const saved = await this.options.gateway.submit(command, key, current);
        if (!current()) throw new AccessFailure(503, "commit_outcome_unknown");
        return saved;
      });
      if (current()) { this.activeReference = null; this.patch({ attempt: null, result, draft: blank(), selected: null, history: empty() }); }
    } catch (error) {
      if (this.active) {
        const uncertain = this.ledger.isUncertain("source");
        this.patch({ attempt: uncertain ? command : null, error });
        if (!uncertain) { this.activeReference = null; this.patch({ selected: null, history: empty() }); }
        this.failed(error);
      }
    } finally { if (this.active) { this.patch({ sending: false }); this.refreshReferences(); this.checkSession(); } }
  }
  setRecovering = (recovering: boolean) => { if (this.active) this.patch({ recovering }); };
  receipt = async (entry: OperationReference): Promise<StockSourceRecoveryReceipt | null> => {
    const generation = this.generation, current = () => this.isCurrent() && generation === this.generation && entry.actorId === this.options.actorId;
    if (!current()) throw new AccessFailure(409, "recovery_request_stale");
    sourceOperation(entry.operation);
    if (!this.state.warehouse) throw new AccessFailure(400, "stock_source_selection_required");
    const result = await this.options.gateway.receipt(entry, this.state.warehouse.location);
    if (!current()) throw new AccessFailure(409, "recovery_request_stale");
    return result === null ? null : { state: "committed", result };
  };
  recoveryFailed = (error: unknown) => { this.failed(error); this.refreshReferences(); };
  reconciled = () => {
    this.refreshReferences(); if (this.state.recoveryError) return;
    const active = this.activeReference;
    if (!active || !this.state.references.some(entry => entry.operation === active.operation && entry.key === active.key)) {
      this.ledger.resetAfterReconciliation(); this.activeReference = null;
      this.patch({ attempt: null, review: null, selected: null, draft: blank(), result: null, history: empty(), error: null });
    }
  };
}
