import { AccessFailure, MutationLedger, businessRefusal } from "../administration/workflow";
import { OperationJournal, submitTrackedRequest, type OperationReference, type StockSourceAliasRecoveryReceipt } from "../administration/operation-journal";
import { IdentityFailure, sessionTiming, type IdentitySession } from "../identity/identity-workflow";
import type { AccessScope } from "../organizations/access-scope";
import { stockPageRequest, type StockWarehouse } from "./stock-count-workflow";
import type { StockSourceGateway } from "./stock-source-client";
import type { StockSourceRevisionGateway } from "./stock-source-revision-client";
import type { StockSourceAliasGateway } from "./stock-source-alias-client";
import { sourceActor, sourceLocationKey, type SourceDecision, type SourcePurpose } from "./stock-source-workflow";
import { parseCurrentSources, type CurrentSource } from "./stock-source-revision-workflow";
import { parseCurrentAlias, parseSourceAlias, parseSourceAliases, prepareSourceAlias, prepareSourceAliasDecision, sourceAliasOperation,
  sourceAliasOutcome, sourceAliasReceipt, type CurrentAlias, type SourceAlias, type SourceAliasCommand } from "./stock-source-alias-workflow";

type Page<T> = { total: string; items: readonly T[] };
type ReadKind = "warehouses" | "sources" | "current" | "history" | "detail";
type Result = ReturnType<typeof sourceAliasOutcome>;
export type AliasEditorSnapshot = Readonly<{
  purpose: SourcePurpose; warehouses: StockWarehouse[]; warehouse: StockWarehouse | null; requested: StockWarehouse | null;
  sources: Page<CurrentSource>; sourceOffset: number; poolId: string; current: CurrentAlias | null;
  history: Page<SourceAlias>; historyOffset: number; selected: SourceAlias | null; enabled: boolean; reason: string;
  review: SourceAliasCommand | null; attempt: SourceAliasCommand | null; result: Result | null; references: OperationReference[];
  recoveryPoolId: string; recoveryWarehouse: StockWarehouse | null;
  loadingWarehouses: boolean; loadingSources: boolean; loadingCurrent: boolean; loadingHistory: boolean; loadingDetail: boolean;
  sending: boolean; recovering: boolean; expired: boolean; suspended: boolean; fresh: boolean; error: unknown; recoveryError: unknown;
}>;
type Options = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number; now: () => number;
  gateway: StockSourceAliasGateway; warehouses: StockSourceGateway["warehouses"]; sources: StockSourceRevisionGateway["sources"];
  journal: OperationJournal; newKey?: () => string };
const empty = <T>(): Page<T> => ({ total: "0", items: [] });
const sameWarehouse = (a: AccessScope, b: AccessScope) => a.organizationId === b.organizationId && a.shopId === b.shopId && a.warehouseId === b.warehouseId;
const badResponse = () => new AccessFailure(502, "stock_source_alias_response_invalid");

/** A mounted account owns its private view; only the original operation reference survives reload. */
export class StockSourceAliasEditor {
  readonly canRead: boolean; readonly canPropose: boolean; readonly canApprove: boolean; readonly journal: OperationJournal;
  private state: AliasEditorSnapshot; private active = false; private generation = 0;
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
      warehouses: [], warehouse: null, requested: null, sources: empty(), sourceOffset: 0, poolId: "", current: null,
      history: empty(), historyOffset: 0, selected: null, enabled: true, reason: "", review: null, attempt: null, result: null, references: [],
      recoveryPoolId: "", recoveryWarehouse: null, loadingWarehouses: false, loadingSources: false, loadingCurrent: false,
      loadingHistory: false, loadingDetail: false, sending: false, recovering: false, expired: false, suspended: false,
      fresh: sessionTiming(this.session, options.now() - options.observedAt).fresh, error: null, recoveryError: null };
  }
  snapshot = (): AliasEditorSnapshot => this.state;
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  private patch(change: Partial<AliasEditorSnapshot>) { this.state = { ...this.state, ...change }; this.listeners.forEach(listener => listener()); }
  isCurrent = () => this.active && !this.state.expired && !this.state.suspended && !sessionTiming(this.session, this.options.now() - this.options.observedAt).expired;
  private guard() { this.checkSession(); return this.isCurrent(); }
  get selectionLocked() { return !this.isCurrent() || this.state.sending || this.state.recovering || this.state.review !== null || this.state.attempt !== null; }
  get locked() { return this.selectionLocked || this.state.references.length > 0 || this.state.recoveryError !== null; }
  get requestedChoices() {
    const owner = this.state.warehouse?.location;
    return owner ? this.state.warehouses.filter(item => sameWarehouse(owner, item.location) && owner.channelId !== item.location.channelId) : [];
  }
  get staleBase() {
    const p = this.state.selected, c = this.state.current;
    return !!p && !!c && (p.poolVersion !== c.source.poolVersion || p.baseAliasVersion !== (c.mapping?.appliedAliasVersion ?? "0"));
  }
  private mayEdit() { return this.guard() && !this.locked; }
  private cancel(kind: ReadKind) { this.reads.get(kind)?.abort(); this.reads.delete(kind); }
  private cancelAll() { for (const kind of this.reads.keys()) this.cancel(kind); }
  private begin(kind: ReadKind) {
    this.cancel(kind); const controller = new AbortController(); this.reads.set(kind, controller);
    return { signal: controller.signal, current: () => this.isCurrent() && this.reads.get(kind) === controller && !controller.signal.aborted };
  }
  private cleared() {
    return { current: null, history: empty<SourceAlias>(), historyOffset: 0, selected: null, enabled: true, reason: "", review: null, result: null,
      loadingCurrent: false, loadingHistory: false, loadingDetail: false };
  }
  private conceal(change: Partial<AliasEditorSnapshot>) {
    this.generation++; this.cancelAll();
    this.patch({ ...this.cleared(), warehouses: [], warehouse: null, requested: null, poolId: "", sources: empty(),
      recoveryPoolId: "", recoveryWarehouse: null, loadingWarehouses: false, loadingSources: false, attempt: null, ...change });
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
        error.code.endsWith("_response_invalid")) this.conceal({ suspended: true, error });
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
    this.patch({ ...this.cleared(), warehouse: null, requested: null, poolId: "", sources: empty(), warehouses: [],
      recoveryPoolId: "", recoveryWarehouse: null, loadingSources: false, loadingWarehouses: true, error: null });
    const ticket = this.begin("warehouses");
    try { const warehouses = await this.options.warehouses(this.options.actorId, this.state.purpose, ticket.signal);
      if (ticket.current()) this.patch({ warehouses }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingWarehouses: false }); this.checkSession(); }
  }
  async chooseWarehouse(location: AccessScope | null) {
    if (!this.mayEdit() || this.state.loadingWarehouses || !this.canRead) return;
    const warehouse = location === null ? null : this.state.warehouses.find(item => sourceLocationKey(item.location) === sourceLocationKey(location));
    if (location !== null && !warehouse) { this.failed(new AccessFailure(400, "stock_source_selection_required")); return; }
    this.generation++; this.cancelAll(); this.patch({ ...this.cleared(), warehouse: warehouse ?? null, requested: null, poolId: "",
      sources: empty(), sourceOffset: 0, loadingSources: false, error: null });
    if (warehouse) await this.loadSources();
  }
  async loadSources(offset = 0) {
    if (!this.mayEdit() || !this.canRead || !this.state.warehouse) return;
    try { stockPageRequest(offset, 25); } catch (error) { this.failed(error); return; }
    this.generation++; this.cancelAll(); const location = this.state.warehouse.location, ticket = this.begin("sources");
    this.patch({ ...this.cleared(), sources: empty(), sourceOffset: offset, poolId: "", requested: null, loadingSources: true, error: null });
    try { const raw = await this.options.sources(this.options.actorId, location, offset, ticket.signal);
      if (ticket.current()) this.patch({ sources: parseCurrentSources(raw, location, offset, 25) }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingSources: false }); this.checkSession(); }
  }
  chooseSource(id: string) {
    if (!this.mayEdit() || !this.canRead || this.state.loadingSources || !this.state.sources.items.some(item => item.physicalPoolId === id)) return;
    this.generation++; this.cancelAll(); this.patch({ ...this.cleared(), poolId: id, requested: null, error: null });
  }
  async chooseRequested(location: AccessScope | null) {
    if (!this.mayEdit() || !this.canRead || !this.state.poolId || !this.state.warehouse) return;
    const requested = location === null ? null : this.requestedChoices.find(item => sourceLocationKey(item.location) === sourceLocationKey(location));
    if (location !== null && !requested) { this.failed(new AccessFailure(400, "stock_source_selection_required")); return; }
    this.generation++; this.cancelAll(); this.patch({ ...this.cleared(), requested: requested ?? null, error: null });
    if (!requested) return;
    const poolId = this.state.poolId, owner = this.state.warehouse.location, ticket = this.begin("current");
    this.patch({ loadingCurrent: true });
    try {
      const current = parseCurrentAlias(await this.options.gateway.current(this.options.actorId, poolId, requested.location, ticket.signal));
      if (!ticket.current()) return;
      if (current.source.physicalPoolId !== poolId || sourceLocationKey(current.source.settings.location) !== sourceLocationKey(owner) ||
        sourceLocationKey(current.requestedLocation) !== sourceLocationKey(requested.location)) throw badResponse();
      this.patch({ current, enabled: current.mapping?.enabled ?? true, loadingCurrent: false }); await this.loadHistory();
    } catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingCurrent: false }); this.checkSession(); }
  }
  async loadHistory(offset = 0) {
    if (!this.mayEdit() || !this.canRead || !this.state.current || !this.state.requested) return;
    try { stockPageRequest(offset, 25); } catch (error) { this.failed(error); return; }
    this.cancel("detail"); const place = this.state.requested.location, poolId = this.state.poolId, ticket = this.begin("history");
    this.patch({ history: empty(), historyOffset: offset, selected: null, loadingDetail: false, loadingHistory: true, error: null });
    try { const raw = await this.options.gateway.history(this.options.actorId, poolId, place, offset, ticket.signal);
      if (ticket.current()) this.patch({ history: parseSourceAliases(raw, poolId, place, offset, 25) }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingHistory: false }); this.checkSession(); }
  }
  async chooseProposal(id: string) {
    if (!this.mayEdit() || !this.canRead || this.state.loadingHistory || !this.state.current || !this.state.requested ||
      !this.state.history.items.some(item => item.id === id)) return;
    const place = this.state.requested.location, poolId = this.state.poolId, owner = this.state.current.source.settings.location,
      physicalKey = this.state.current.source.settings.physicalKey, ticket = this.begin("detail");
    this.patch({ selected: null, loadingDetail: true, error: null });
    try {
      const raw = await this.options.gateway.detail(this.options.actorId, poolId, place, id, ticket.signal);
      if (!ticket.current()) return;
      if (raw === null) throw new AccessFailure(409, "stock_source_alias_changed");
      const selected = parseSourceAlias(raw);
      if (selected.id !== id || selected.physicalPoolId !== poolId || selected.physicalKey !== physicalKey || sourceLocationKey(selected.requestedLocation) !== sourceLocationKey(place) ||
        sourceLocationKey(selected.ownerLocation) !== sourceLocationKey(owner)) throw badResponse();
      this.patch({ selected });
    } catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingDetail: false }); this.checkSession(); }
  }
  changeDraft(enabled: boolean, reason: string) {
    if (this.mayEdit() && this.canRead && this.canPropose && this.state.current) this.patch({ enabled, reason, error: null });
  }
  reviewProposal() {
    this.refreshReferences(); if (!this.mayEdit()) return;
    try {
      if (!this.canRead || !this.canPropose) throw new AccessFailure(403, "access_denied");
      if (!this.state.current || this.state.loadingCurrent) throw new AccessFailure(400, "stock_source_selection_required");
      this.patch({ review: prepareSourceAlias(this.options.actorId, this.state.current, this.state.enabled, this.state.reason), result: null, error: null });
    } catch (error) { this.failed(error); }
  }
  reviewDecision(action: SourceDecision, reference: string, reason: string) {
    this.refreshReferences(); if (!this.mayEdit()) return;
    try {
      if (!this.canRead || (action === "withdraw" ? !this.canPropose : !this.canApprove)) throw new AccessFailure(403, "access_denied");
      if (!this.state.selected || !this.state.current || this.state.loadingDetail || this.state.loadingCurrent) throw new AccessFailure(400, "stock_source_selection_required");
      if (action !== "withdraw" && !this.state.fresh) throw new AccessFailure(403, "step_up_required");
      if (action === "approve" && (this.staleBase || this.state.current.source.poolState !== "inactive")) throw new AccessFailure(409, "stock_source_alias_not_approvable");
      this.patch({ review: prepareSourceAliasDecision(this.options.actorId, this.state.selected, action, reference, reason), result: null, error: null });
    } catch (error) { this.failed(error); }
  }
  cancelReview() { if (this.guard() && !this.state.sending && !this.state.recovering) this.patch({ review: null, error: null }); }
  async submitReviewed(confirmed: boolean) {
    if (!confirmed || !this.state.review || !this.guard() || this.state.sending || this.state.recovering || this.state.attempt) return;
    this.refreshReferences(); if (this.state.references.length || this.state.recoveryError) return;
    const command = this.state.review;
    if ((command.operation === "stock.source.alias.approve" || command.operation === "stock.source.alias.reject") && !this.state.fresh) {
      this.failed(new AccessFailure(403, "step_up_required")); return;
    }
    const generation = this.generation, isCurrent = () => this.isCurrent() && generation === this.generation;
    this.cancelAll(); this.patch({ sending: true, review: null, attempt: command, error: null, result: null, loadingHistory: false, loadingDetail: false,
      recoveryPoolId: command.poolId, recoveryWarehouse: this.state.requested });
    try {
      const result = await submitTrackedRequest(this.ledger, this.journal, this.options.actorId, "alias", command.operation, command, async key => {
        this.activeReference = { operation: command.operation, key };
        const saved = await this.options.gateway.submit(command, key, isCurrent);
        if (!isCurrent()) throw new AccessFailure(503, "commit_outcome_unknown");
        return sourceAliasOutcome(saved, command);
      });
      if (isCurrent()) { this.activeReference = null; this.patch({ ...this.cleared(), sources: empty(), poolId: "", requested: null, attempt: null, result }); }
    } catch (error) {
      if (this.active) { const uncertain = this.ledger.isUncertain("alias");
        this.patch({ attempt: uncertain ? command : null, error }); if (!uncertain) this.activeReference = null; this.failed(error); }
    } finally { if (this.active) { this.patch({ sending: false }); this.refreshReferences(); this.checkSession(); } }
  }
  setRecoveryContext(poolId: string, location: AccessScope | null) {
    if (!this.guard() || this.state.sending || this.state.recovering || this.state.review) return;
    const warehouse = location === null ? null : this.state.warehouses.find(item => sourceLocationKey(item.location) === sourceLocationKey(location));
    if (location !== null && !warehouse) { this.failed(new AccessFailure(400, "stock_source_selection_required")); return; }
    this.generation++; this.patch({ recoveryPoolId: poolId, recoveryWarehouse: warehouse ?? null, error: null });
  }
  setRecovering = (recovering: boolean) => { if (this.active) this.patch({ recovering }); };
  receipt = async (entry: OperationReference): Promise<StockSourceAliasRecoveryReceipt | null> => {
    if (!this.guard() || entry.actorId !== this.options.actorId || !this.state.recoveryWarehouse ||
      !this.state.recoveryPoolId || this.state.sending || this.state.review) throw new AccessFailure(409, "recovery_request_stale");
    const operation = sourceAliasOperation(entry.operation), poolId = sourceActor(this.state.recoveryPoolId),
      place = this.state.recoveryWarehouse.location, generation = this.generation;
    if (operation === "stock.source.alias.propose" || operation === "stock.source.alias.withdraw" ? !this.canPropose : !this.canApprove)
      throw new AccessFailure(403, "access_denied");
    const raw = await this.options.gateway.receipt(entry, poolId, place);
    if (!this.guard() || generation !== this.generation) throw new AccessFailure(409, "recovery_request_stale");
    if (raw === null) return null;
    const result = sourceAliasReceipt({ state: "committed", result: raw }, operation)!;
    if (result.physicalPoolId !== poolId) throw badResponse();
    if (this.activeReference?.operation === entry.operation && this.activeReference.key === entry.key && this.state.attempt)
      sourceAliasOutcome(result, this.state.attempt);
    return { state: "committed", result };
  };
  recoveryFailed = (error: unknown) => { this.failed(error); this.refreshReferences(); };
  reconciled = () => {
    this.refreshReferences(); if (this.state.recoveryError) return;
    const pending = this.activeReference;
    if (!pending || !this.state.references.some(entry => entry.operation === pending.operation && entry.key === pending.key)) {
      this.ledger.resetAfterReconciliation(); this.activeReference = null;
      this.patch({ ...this.cleared(), sources: empty(), poolId: "", requested: null, attempt: null, error: null });
    }
  };
}
