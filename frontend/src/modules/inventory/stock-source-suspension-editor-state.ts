import { AccessFailure, MutationLedger, stableContent } from "../administration/workflow";
import { OperationJournal, submitTrackedRequest, type OperationReference, type StockSourceSuspensionRecoveryReceipt } from "../administration/operation-journal";
import { IdentityFailure, sessionTiming, type IdentitySession } from "../identity/identity-workflow";
import type { StockSourceSuspensionGateway } from "./stock-source-suspension-client";
import { sourceActor } from "./stock-source-workflow";
import { stockPageRequest } from "./stock-count-workflow";
import { parseSuspensionPreview, parseSourceSuspension, parseSourceSuspensions, prepareSourceSuspension,
  suspensionOutcome, suspensionReceipt, type SuspensionPreview, type SourceSuspension, type SourceSuspensionCommand } from "./stock-source-suspension-workflow";

type Result = NonNullable<ReturnType<typeof suspensionReceipt>>;
type Page = { total: string; items: SourceSuspension[] };
const empty = (): Page => ({ total: "0", items: [] });
type Snapshot = {
  poolInput: string; poolId: string; preview: SuspensionPreview | null; history: Page; historyOffset: number; selected: SourceSuspension | null;
  reason: string; review: SourceSuspensionCommand | null; attempt: SourceSuspensionCommand | null; result: Result | null;
  references: OperationReference[]; recoveryPoolId: string; loadingPool: boolean; loadingHistory: boolean; loadingDetail: boolean;
  sending: boolean; recovering: boolean; fresh: boolean; expired: boolean; suspended: boolean; error: unknown; recoveryError: unknown;
};
type Options = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number; now: () => number;
  gateway: StockSourceSuspensionGateway; journal: OperationJournal; newKey?: () => string };
type ReadKind = "pool" | "history" | "detail";
const invalid = () => new AccessFailure(502, "stock_source_suspension_response_invalid");

/** Private views live only in this account's mount; recovery storage contains references only. */
export class StockSourceSuspensionEditor {
  readonly canRead: boolean; readonly canSuspend: boolean; readonly journal: OperationJournal;
  private state: Snapshot; private active = false; private generation = 0;
  private readonly reads = new Map<ReadKind, AbortController>(); private readonly listeners = new Set<() => void>();
  private readonly session: IdentitySession; private readonly ledger: MutationLedger;
  private activeReference: { operation: string; key: string } | null = null;
  constructor(private readonly options: Options) {
    sourceActor(options.actorId);
    if (!options.session.authenticated || options.session.account?.userId !== options.actorId) throw new AccessFailure(409, "actor_changed");
    this.canRead = options.actions.includes("stock.source.read"); this.canSuspend = options.actions.includes("stock.source.suspend");
    if (!this.canRead && !this.canSuspend) throw new AccessFailure(403, "access_denied");
    this.session = structuredClone(options.session); this.journal = options.journal; this.ledger = new MutationLedger(options.newKey);
    this.state = { poolInput: "", poolId: "", preview: null, history: empty(), historyOffset: 0, selected: null, reason: "", review: null,
      attempt: null, result: null, references: [], recoveryPoolId: "", loadingPool: false, loadingHistory: false, loadingDetail: false,
      sending: false, recovering: false, fresh: sessionTiming(this.session, options.now() - options.observedAt).fresh,
      expired: false, suspended: false, error: null, recoveryError: null };
  }
  snapshot = (): Snapshot => this.state;
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  private patch(change: Partial<Snapshot>) { this.state = { ...this.state, ...change }; this.listeners.forEach(listener => listener()); }
  isCurrent = () => this.active && !this.state.expired && !this.state.suspended && !sessionTiming(this.session, this.options.now() - this.options.observedAt).expired;
  private guard() { this.checkSession(); return this.isCurrent(); }
  get selectionLocked() { return !this.isCurrent() || this.state.sending || this.state.recovering || this.state.review !== null || this.state.attempt !== null; }
  get locked() { return this.selectionLocked || this.state.references.length > 0 || this.state.recoveryError !== null; }
  private mayEdit() { return this.guard() && !this.locked; }
  private cancel(kind: ReadKind) { this.reads.get(kind)?.abort(); this.reads.delete(kind); }
  private cancelAll() { for (const kind of this.reads.keys()) this.cancel(kind); }
  private begin(kind: ReadKind) {
    this.cancel(kind); const controller = new AbortController(), generation = this.generation; this.reads.set(kind, controller);
    return { signal: controller.signal, current: () => this.isCurrent() && generation === this.generation && this.reads.get(kind) === controller && !controller.signal.aborted };
  }
  private cleared(): Partial<Snapshot> {
    return { preview: null, history: empty(), historyOffset: 0, selected: null, reason: "", review: null, result: null,
      loadingPool: false, loadingHistory: false, loadingDetail: false };
  }
  private conceal(change: Partial<Snapshot>) {
    this.generation++; this.cancelAll();
    this.patch({ ...this.cleared(), poolInput: "", poolId: "", recoveryPoolId: "", attempt: null, ...change });
  }
  checkSession() {
    const timing = sessionTiming(this.session, this.options.now() - this.options.observedAt);
    if (this.active && !this.state.expired && timing.expired) this.conceal({ expired: true, fresh: false, error: new IdentityFailure(401, "session_expired") });
    else if (this.active && this.state.fresh !== timing.fresh) this.patch({ fresh: timing.fresh, ...(!timing.fresh ? { preview: null, review: null } : {}) });
  }
  dispose() { this.active = false; this.conceal({}); }
  async start() { if (this.active) return; this.active = true; this.refreshReferences(); this.checkSession(); }
  refreshReferences = () => {
    try { this.patch({ references: this.journal.list(this.options.actorId), recoveryError: null }); }
    catch (error) { this.patch({ recoveryError: error }); }
  };
  private failed(error: unknown) {
    if (!this.active) return;
    this.patch({ error });
    if (error instanceof AccessFailure || error instanceof IdentityFailure) {
      if (error.code === "step_up_required") this.patch({ fresh: false, preview: null, review: null });
      else if (error.status === 401 || error.status === 403 || error.code === "actor_changed" || error.code.endsWith("_response_invalid"))
        this.conceal({ suspended: true, error });
    }
  }
  changePool(value: string) {
    if (!this.mayEdit()) return;
    this.generation++; this.cancelAll(); this.patch({ ...this.cleared(), poolInput: value, poolId: "", error: null });
  }
  async loadPool() {
    if (!this.mayEdit()) return;
    try {
      const poolId = sourceActor(this.state.poolInput);
      if (this.canSuspend && !this.canRead && !this.state.fresh) throw new AccessFailure(403, "step_up_required");
      this.generation++; this.cancelAll(); this.patch({ ...this.cleared(), poolId, loadingPool: true, error: null });
      const ticket = this.begin("pool");
      try {
        if (this.canSuspend && this.state.fresh) {
          const preview = parseSuspensionPreview(await this.options.gateway.preview(this.options.actorId, poolId, ticket.signal));
          if (!ticket.current()) return;
          if (preview.physicalPoolId !== poolId) throw invalid();
          this.patch({ preview });
        }
        if (ticket.current() && this.canRead) await this.loadHistory();
      } catch (error) { if (ticket.current()) this.failed(error); }
      finally { if (ticket.current()) this.patch({ loadingPool: false }); this.checkSession(); }
    } catch (error) { this.failed(error); }
  }
  async loadHistory(offset = 0) {
    if (!this.mayEdit() || !this.canRead || !this.state.poolId) return;
    try { stockPageRequest(offset, 25); } catch (error) { this.failed(error); return; }
    this.cancel("detail"); const poolId = this.state.poolId, ticket = this.begin("history");
    this.patch({ history: empty(), historyOffset: offset, selected: null, loadingDetail: false, loadingHistory: true, error: null });
    try {
      const raw = await this.options.gateway.history(this.options.actorId, poolId, offset, ticket.signal);
      if (ticket.current()) this.patch({ history: parseSourceSuspensions(raw, poolId, offset, 25) });
    } catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingHistory: false }); this.checkSession(); }
  }
  async chooseHistory(id: string) {
    if (!this.mayEdit() || !this.canRead || this.state.loadingHistory) return;
    const expected = this.state.history.items.find(item => item.id === id);
    if (!expected) return;
    const poolId = this.state.poolId, ticket = this.begin("detail"); this.patch({ selected: null, loadingDetail: true, error: null });
    try {
      const raw = await this.options.gateway.detail(this.options.actorId, poolId, id, ticket.signal);
      if (!ticket.current()) return;
      if (raw === null) throw new AccessFailure(409, "stock_source_suspension_changed");
      const selected = parseSourceSuspension(raw);
      if (stableContent(selected) !== stableContent(expected)) throw invalid();
      this.patch({ selected });
    } catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingDetail: false }); this.checkSession(); }
  }
  changeReason(reason: string) { if (this.mayEdit() && this.canSuspend && this.state.preview) this.patch({ reason, error: null }); }
  reviewStop() {
    this.refreshReferences(); if (!this.mayEdit()) return;
    try {
      if (!this.canSuspend) throw new AccessFailure(403, "access_denied");
      if (!this.state.fresh) throw new AccessFailure(403, "step_up_required");
      if (!this.state.preview || this.state.loadingPool) throw new AccessFailure(400, "stock_source_selection_required");
      this.patch({ review: prepareSourceSuspension(this.options.actorId, this.state.preview, this.state.reason), result: null, error: null });
    } catch (error) { this.failed(error); }
  }
  cancelReview() { if (this.guard() && !this.state.sending && !this.state.recovering) this.patch({ review: null, error: null }); }
  async submitReviewed(confirmed: boolean) {
    if (confirmed !== true || !this.state.review || !this.guard() || this.state.sending || this.state.recovering || this.state.attempt) return;
    this.refreshReferences(); if (this.state.references.length || this.state.recoveryError) return;
    if (!this.state.fresh) { this.failed(new AccessFailure(403, "step_up_required")); return; }
    const command = this.state.review, generation = this.generation, isCurrent = () => this.isCurrent() && generation === this.generation && sessionTiming(this.session, this.options.now() - this.options.observedAt).fresh;
    this.cancelAll(); this.patch({ sending: true, review: null, attempt: command, error: null, result: null, recoveryPoolId: command.poolId,
      loadingPool: false, loadingHistory: false, loadingDetail: false });
    try {
      const result = await submitTrackedRequest(this.ledger, this.journal, this.options.actorId, "suspension", command.operation, command, async key => {
        this.activeReference = { operation: command.operation, key };
        const saved = await this.options.gateway.submit(command, key, isCurrent);
        if (!isCurrent()) throw new AccessFailure(503, "commit_outcome_unknown");
        return suspensionOutcome(saved, command);
      });
      if (isCurrent()) { this.activeReference = null; this.patch({ ...this.cleared(), poolInput: "", poolId: "", attempt: null, result }); }
    } catch (error) {
      if (this.active) {
        const uncertain = this.ledger.isUncertain("suspension");
        this.patch({ attempt: uncertain && this.isCurrent() ? command : null, preview: null, selected: null, error });
        if (!uncertain) this.activeReference = null; this.failed(error);
      }
    } finally { if (this.active) { this.patch({ sending: false }); this.refreshReferences(); this.checkSession(); } }
  }
  setRecoveryPool(poolId: string) {
    if (!this.guard() || this.state.sending || this.state.recovering || this.state.review) return;
    this.generation++; this.cancelAll(); this.patch({ recoveryPoolId: poolId, loadingPool: false, loadingHistory: false, loadingDetail: false, error: null });
  }
  setRecovering = (recovering: boolean) => { if (this.active) this.patch({ recovering }); };
  receipt = async (entry: OperationReference): Promise<StockSourceSuspensionRecoveryReceipt | null> => {
    if (!this.guard() || entry.actorId !== this.options.actorId || entry.operation !== "stock.source.suspend" ||
      !this.state.recoveryPoolId || this.state.sending || this.state.review) throw new AccessFailure(409, "recovery_request_stale");
    if (!this.canSuspend) throw new AccessFailure(403, "access_denied");
    if (!this.state.fresh) throw new AccessFailure(403, "step_up_required");
    const poolId = sourceActor(this.state.recoveryPoolId), generation = this.generation;
    const raw = await this.options.gateway.receipt(entry, poolId);
    if (!this.guard() || generation !== this.generation) throw new AccessFailure(409, "recovery_request_stale");
    if (!this.state.fresh) throw new AccessFailure(403, "step_up_required");
    if (raw === null) return null;
    const result = suspensionReceipt({ state: "committed", result: raw })!;
    if (result.physicalPoolId !== poolId) throw invalid();
    if (this.activeReference?.operation === entry.operation && this.activeReference.key === entry.key && this.state.attempt)
      suspensionOutcome(result, this.state.attempt);
    return { state: "committed", result };
  };
  recoveryFailed = (error: unknown) => { this.failed(error); this.refreshReferences(); };
  reconciled = () => {
    this.refreshReferences(); if (this.state.recoveryError) return;
    const pending = this.activeReference;
    if (!pending || !this.state.references.some(entry => entry.operation === pending.operation && entry.key === pending.key)) {
      this.ledger.resetAfterReconciliation(); this.activeReference = null;
      this.patch({ ...this.cleared(), poolInput: "", poolId: "", attempt: null, recoveryPoolId: "", error: null });
    }
  };
}
