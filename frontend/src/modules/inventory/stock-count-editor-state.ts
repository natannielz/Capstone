import { AccessFailure, MutationLedger } from "../administration/workflow";
import { OperationJournal, submitTrackedRequest, type OperationReference } from "../administration/operation-journal";
import { IdentityFailure, sessionTiming, type IdentitySession } from "../identity/identity-workflow";
import type { AccessScope } from "../organizations/access-scope";
import type { StockCountGateway } from "./stock-count-client";
import {
  stockCountActor, stockCountOperation, stockLocationKey, stockCountSearch, stockPageRequest, prepareStockCount,
  type StockCountDraft, type StockCountCommand, type StockWarehouse, type StockPackageChoice, type StockObservation, type StockSourceChoice,
} from "./stock-count-workflow";

type Page<T> = { total: string; items: T[] };
type Mode = "count" | "history";
type ReadKind = "warehouses" | "sources" | "packages" | "history";
export type StockEditorSnapshot = Readonly<{
  mode: Mode; warehouses: StockWarehouse[]; warehouse: StockWarehouse | null; search: string;
  packages: Page<StockPackageChoice>; packageOffset: number; selectedPackage: StockPackageChoice | null;
  sources: Page<StockSourceChoice>; sourceOffset: number; selectedSource: StockSourceChoice | null; sourceMode: 'bound' | 'unbound' | null;
  history: Page<StockObservation>; historyOffset: number; draft: StockCountDraft;
  review: StockCountCommand | null; attempt: StockCountCommand | null; result: StockObservation | null;
  loadingWarehouses: boolean; loadingSources: boolean; loadingPackages: boolean; loadingHistory: boolean; sending: boolean; recovering: boolean;
  expired: boolean; suspended: boolean; error: unknown; recoveryError: unknown; references: OperationReference[];
}>;
type Options = {
  actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number; now: () => number;
  gateway: StockCountGateway; journal: OperationJournal; newKey?: () => string;
};
const emptyPage = <T>(): Page<T> => ({ total: "0", items: [] });
const blank = (): StockCountDraft => ({ packages: "", reason: "", observedLotLabel: "" });
/** One mounted account view owns reads, review and its original in-memory submission. */
export class StockCountEditor {
  readonly canCount: boolean;
  readonly canRead: boolean;
  readonly journal: OperationJournal;
  private state: StockEditorSnapshot;
  private active = false;
  private generation = 0;
  private readonly reads = new Map<ReadKind, AbortController>();
  private readonly listeners = new Set<() => void>();
  private readonly ledger: MutationLedger;
  private readonly session: IdentitySession;
  private activeKey: string | null = null;

  constructor(private readonly options: Options) {
    stockCountActor(options.actorId);
    if (!options.session.authenticated || options.session.account?.userId !== options.actorId)
      throw new AccessFailure(409, "actor_changed");
    this.canCount = options.actions.includes("stock.count");
    this.canRead = options.actions.includes("stock.read");
    if (!this.canCount && !this.canRead) throw new AccessFailure(403, "access_denied");
    this.session = structuredClone(options.session);
    this.journal = options.journal;
    this.ledger = new MutationLedger(options.newKey);
    this.state = { mode: this.canCount ? "count" : "history", warehouses: [], warehouse: null, search: "",
      packages: emptyPage(), packageOffset: 0, selectedPackage: null, history: emptyPage(), historyOffset: 0,
      sources: emptyPage(), sourceOffset: 0, selectedSource: null, sourceMode: null, loadingSources: false,
      draft: blank(), review: null, attempt: null, result: null, loadingWarehouses: false, loadingPackages: false,
      loadingHistory: false, sending: false, recovering: false, expired: false, suspended: false,
      error: null, recoveryError: null, references: [] };
  }
  snapshot = (): StockEditorSnapshot => this.state;
  subscribe = (listener: () => void): (() => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  private patch(change: Partial<StockEditorSnapshot>) {
    this.state = { ...this.state, ...change }; this.listeners.forEach(listener => listener());
  }
  isCurrent = (): boolean => this.active && !this.state.expired && !this.state.suspended &&
    !sessionTiming(this.session, this.options.now() - this.options.observedAt).expired;
  private guard(): boolean { this.checkSession(); return this.isCurrent(); }
  get locked(): boolean {
    return !this.isCurrent() || this.state.sending || this.state.recovering || this.state.review !== null ||
      this.state.attempt !== null || this.state.references.length > 0 || this.state.recoveryError !== null;
  }
  private cancel(kind: ReadKind) { this.reads.get(kind)?.abort(); this.reads.delete(kind); }
  private cancelAll() { for (const kind of this.reads.keys()) this.cancel(kind); }
  private begin(kind: ReadKind) {
    this.cancel(kind); const controller = new AbortController(); this.reads.set(kind, controller);
    return { signal: controller.signal, current: () => this.isCurrent() && this.reads.get(kind) === controller && !controller.signal.aborted };
  }
  private cleared() {
    return { warehouse: null, search: "", packages: emptyPage<StockPackageChoice>(), packageOffset: 0,
      selectedPackage: null, history: emptyPage<StockObservation>(), historyOffset: 0,
      sources: emptyPage<StockSourceChoice>(), sourceOffset: 0, selectedSource: null, sourceMode: null, loadingSources: false,
      draft: blank(), review: null, result: null, loadingPackages: false, loadingHistory: false };
  }
  private conceal(change: Partial<StockEditorSnapshot>) {
    this.generation++; this.cancelAll();
    this.patch({ ...this.cleared(), warehouses: [], loadingWarehouses: false, ...change });
  }
  checkSession() {
    if (this.active && !this.state.expired && sessionTiming(this.session, this.options.now() - this.options.observedAt).expired)
      this.conceal({ expired: true, error: new IdentityFailure(401, "session_expired") });
  }
  dispose() { this.active = false; this.conceal({}); }
  async start() {
    if (this.active) return;
    this.active = true; this.refreshReferences();
    if (this.guard()) await this.loadWarehouses();
  }
  refreshReferences = () => {
    try { this.patch({ references: this.journal.list(this.options.actorId), recoveryError: null }); }
    catch (error) { this.patch({ recoveryError: error }); }
  };
  private failed(error: unknown) {
    if (!this.active) return;
    this.patch({ error });
    if (error instanceof AccessFailure || error instanceof IdentityFailure) {
      if (error.status === 401 || error.code === "actor_changed") this.conceal({ suspended: true, error });
      else if (error.status === 403 && error.code !== 'step_up_required' || error.code === "stock_count_response_invalid") this.conceal({ suspended: true, error });
    }
  }
  private mayEdit() { return this.guard() && !this.locked; }
  private mayCount(): boolean {
    return this.guard() && this.canCount;
  }
  async changeMode(mode: Mode) {
    if (!this.mayEdit() || mode === this.state.mode) return;
    if (mode === "count" ? !this.canCount : !this.canRead) { this.patch({ error: new AccessFailure(403, "access_denied") }); return; }
    this.patch({ mode }); await this.loadWarehouses();
  }
  async loadWarehouses() {
    if (!this.guard() || this.state.sending || this.state.recovering || this.state.attempt || this.state.review) return;
    this.cancelAll(); this.patch({ ...this.cleared(), warehouses: [], loadingWarehouses: true, error: null });
    const ticket = this.begin("warehouses"), purpose = this.state.mode === "count" ? "inventory-stock-count" : "inventory-stock-history";
    try {
      const warehouses = await this.options.gateway.warehouses(this.options.actorId, purpose, ticket.signal);
      if (ticket.current()) this.patch({ warehouses });
    } catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingWarehouses: false }); this.checkSession(); }
  }
  async chooseWarehouse(location: AccessScope | null) {
    if (!this.mayEdit() || this.state.loadingWarehouses) return;
    const warehouse = location === null ? null : this.state.warehouses.find(item => stockLocationKey(item.location) === stockLocationKey(location));
    if (location !== null && !warehouse) { this.patch({ error: new AccessFailure(400, "stock_count_selection_required") }); return; }
    this.cancel("sources"); this.cancel("packages"); this.cancel("history");
    this.patch({ ...this.cleared(), warehouse: warehouse ?? null, error: null });
    if (warehouse) {
      if (this.state.mode === "count") await Promise.all([this.loadSources(), this.loadPackages()]); else await this.loadHistory();
    }
  }
  async loadSources(offset = 0) {
    if (!this.mayEdit() || !this.mayCount() || this.state.mode !== 'count' || !this.state.warehouse) return;
    try { stockPageRequest(offset, 25); } catch (error) { this.failed(error); return; }
    const location = this.state.warehouse.location, ticket = this.begin('sources');
    this.patch({ sources: emptyPage(), selectedSource: null, sourceMode: null, sourceOffset: offset, loadingSources: true,
      selectedPackage: null, draft: blank(), result: null, error: null });
    const current = ticket.current;
    try {
      const sources = await this.options.gateway.sources(this.options.actorId, location, offset, ticket.signal, current);
      if (current()) this.patch({ sources });
    } catch (error) {
      if (ticket.current()) {
        // No trustworthy source choice is available. Existing explicit unbound observations
        // remain legitimate; permission/identity denial still conceals the account's data.
        if ((error instanceof AccessFailure || error instanceof IdentityFailure) &&
            (error.status === 401 || error.code === 'actor_changed' || error.status === 403 && error.code !== 'step_up_required')) this.failed(error);
        else this.patch({ error, selectedSource: null, sourceMode: null });
      }
    }
    finally { if (ticket.current()) this.patch({ loadingSources: false }); this.checkSession(); }
  }
  chooseSource(key: string) {
    if (!this.mayEdit() || !this.mayCount() || this.state.loadingSources || this.state.mode !== 'count' || !this.state.warehouse) return;
    const selectedSource = this.state.sources.items.find(item => item.pool.poolId === key);
    if (key !== 'unbound' && key !== '' && !selectedSource) { this.patch({ error: new AccessFailure(400, 'stock_count_source_selection_required') }); return; }
    this.patch({ selectedSource: selectedSource ?? null, sourceMode: key === 'unbound' ? 'unbound' : selectedSource ? 'bound' : null,
      selectedPackage: null, draft: blank(), result: null, error: null });
  }
  changeSearch(value: string) {
    if (!this.mayEdit() || !this.mayCount()) return;
    this.cancel("packages");
    this.patch({ search: value, packages: emptyPage(), selectedPackage: null, packageOffset: 0, draft: blank(), result: null, loadingPackages: false, error: null });
  }
  async loadPackages(offset = 0) {
    if (!this.mayEdit() || !this.canCount || this.state.mode !== "count" || !this.state.warehouse) return;
    const location = this.state.warehouse.location;
    let search: string;
    try { search = stockCountSearch(this.state.search); stockPageRequest(offset, 25); }
    catch (error) { this.failed(error); return; }
    const ticket = this.begin("packages");
    this.patch({ packages: emptyPage(), selectedPackage: null, draft: blank(), result: null, loadingPackages: true, error: null, packageOffset: offset });
    try {
      const packages = await this.options.gateway.packages(this.options.actorId, location, search, offset, ticket.signal);
      if (ticket.current()) this.patch({ packages });
    } catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingPackages: false }); this.checkSession(); }
  }
  async loadHistory(offset = 0) {
    if (!this.guard() || this.state.sending || this.state.recovering || !this.canRead || this.state.mode !== "history" || !this.state.warehouse) return;
    try { stockPageRequest(offset, 25); } catch (error) { this.failed(error); return; }
    const location = this.state.warehouse.location, ticket = this.begin("history");
    this.patch({ history: emptyPage(), historyOffset: offset, loadingHistory: true, error: null });
    try {
      const history = await this.options.gateway.history(this.options.actorId, location, offset, ticket.signal);
      if (ticket.current()) this.patch({ history });
    } catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingHistory: false }); this.checkSession(); }
  }
  choosePackage(skuId: string) {
    if (!this.mayEdit() || this.state.loadingPackages || this.state.mode !== "count") return;
    const selectedPackage = this.state.packages.items.find(item => item.skuId === skuId);
    if (!selectedPackage) { this.patch({ selectedPackage: null, error: new AccessFailure(400, "stock_count_selection_required") }); return; }
    this.patch({ selectedPackage, draft: blank(), result: null, error: null });
  }
  changeDraft(field: keyof StockCountDraft, value: string) {
    if (this.mayEdit()) this.patch({ draft: { ...this.state.draft, [field]: value }, error: null });
  }
  reviewCount() {
    this.refreshReferences();
    if (!this.mayEdit() || !this.mayCount()) return;
    try {
      const { warehouse, selectedPackage, packages, draft, selectedSource, sourceMode } = this.state;
      if (!this.canCount || this.state.mode !== "count" || !warehouse || !selectedPackage || !packages.items.includes(selectedPackage))
        throw new AccessFailure(400, "stock_count_selection_required");
      if (this.state.loadingSources || sourceMode === null || sourceMode === 'bound' && (!selectedSource || !this.state.sources.items.includes(selectedSource)))
        throw new AccessFailure(400, 'stock_count_source_selection_required');
      const review = prepareStockCount(this.options.actorId, warehouse.location, selectedPackage, draft, selectedSource ?? undefined);
      this.patch({ review, error: null, result: null });
    } catch (error) { this.failed(error); }
  }
  cancelReview() { if (this.guard() && !this.state.sending && !this.state.recovering) this.patch({ review: null, error: null }); }
  async submitReviewed(confirmed: boolean) {
    if (!confirmed || !this.state.review || !this.mayCount() || this.state.sending || this.state.recovering) return;
    this.refreshReferences();
    if (this.state.references.length || this.state.recoveryError) return;
    await this.send(this.state.review);
  }
  async retry() {
    if (!this.state.attempt || !this.mayCount() || this.state.sending || this.state.recovering || !this.ledger.isUncertain("count")) return;
    await this.send(this.state.attempt);
  }
  private async send(command: StockCountCommand) {
    if (command.actorId !== this.options.actorId) return;
    const generation = this.generation;
    const current = () => this.isCurrent() && generation === this.generation;
    this.patch({ sending: true, review: null, attempt: command, error: null, result: null });
    try {
      const result = await submitTrackedRequest(this.ledger, this.journal, this.options.actorId, "count", stockCountOperation, command,
        async key => {
          this.activeKey = key;
          const saved = await this.options.gateway.submit(command, key, current);
          if (!current()) throw new AccessFailure(503, "commit_outcome_unknown");
          return saved;
        });
      if (current()) {
        this.activeKey = null;
        this.patch({ attempt: null, result, selectedPackage: null, draft: blank() });
      }
    } catch (error) {
      if (this.active) {
        this.patch({ attempt: this.ledger.isUncertain("count") ? command : null, error });
        if (!this.ledger.isUncertain("count")) {
          this.activeKey = null;
          if (error instanceof AccessFailure && error.status === 409) {
            this.cancel("sources"); this.cancel("packages"); this.patch({ selectedSource: null, sourceMode: null, sources: emptyPage(), selectedPackage: null, packages: emptyPage(), draft: blank() });
          }
        }
        this.failed(error);
      }
    } finally {
      if (this.active) { this.patch({ sending: false }); this.refreshReferences(); this.checkSession(); }
    }
  }
  setRecovering = (recovering: boolean) => { if (this.active) this.patch({ recovering }); };
  receipt = async (entry: OperationReference) => {
    if (!this.canCount) throw new AccessFailure(403, 'access_denied');
    const generation = this.generation;
    const current = () => this.isCurrent() && generation === this.generation && entry.actorId === this.options.actorId;
    if (!current()) throw new AccessFailure(409, "recovery_request_stale");
    const receipt = await this.options.gateway.receipt(entry);
    if (!current()) throw new AccessFailure(409, "recovery_request_stale");
    return receipt;
  };
  recoveryFailed = (error: unknown) => { this.failed(error); this.refreshReferences(); };
  reconciled = () => {
    this.refreshReferences();
    if (this.state.recoveryError) return;
    if (!this.activeKey || !this.state.references.some(entry => entry.operation === stockCountOperation && entry.key === this.activeKey)) {
      this.ledger.resetAfterReconciliation(); this.activeKey = null;
      this.patch({ attempt: null, review: null, selectedPackage: null, draft: blank(), result: null, error: null });
    }
  };
}
