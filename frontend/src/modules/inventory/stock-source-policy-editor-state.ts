import type { components } from "../../api/generated/schema";
import { AccessFailure, MutationLedger, stableContent } from "../administration/workflow";
import { OperationJournal, submitTrackedRequest, type OperationReference, type StockSourcePolicyRecoveryReceipt } from "../administration/operation-journal";
import { IdentityFailure, sessionTiming, type IdentitySession } from "../identity/identity-workflow";
import type { StockSourcePolicyGateway } from "./stock-source-policy-client";
import { sourceActor, type SourceDecision } from "./stock-source-workflow";
import { stockPageRequest } from "./stock-count-workflow";
import { parsePolicyPreview, parsePolicyReview, parsePolicyLatest, parsePolicyHistory, parseSourcePolicy,
  preparePolicyProposal, preparePolicyDecision, policyOutcome, policyReceipt, sourcePolicyOperation, policyAction, type SourcePolicyCommand } from "./stock-source-policy-workflow";

type Wire = components["schemas"];
type Preview = Wire["StockSourcePolicyPreviewDto"];
type ActionPreview = Wire["StockSourcePolicyReviewDto"];
type Detail = Wire["StockSourcePolicyDetailDto"];
type Result = Wire["StockSourcePolicyCommandDto"];
export type PolicyDraft = { writer: string; externalTenant: string | null; feedIntervalMilliseconds: string;
  reconciliationProcedureReference: string; outageProcedureReference: string; reason: string };
const draft = (): PolicyDraft => ({ writer: "", externalTenant: null, feedIntervalMilliseconds: "", reconciliationProcedureReference: "", outageProcedureReference: "", reason: "" });
const empty = (): Wire["StockSourcePolicyPageDto"] => ({ total: "0", items: [] });
type Snapshot = {
  poolInput: string; poolId: string; proposalInput: string; action: SourceDecision; preview: Preview | null; actionPreview: ActionPreview | null;
  latest: Wire["StockSourcePolicyLatestDto"] | null; history: Wire["StockSourcePolicyPageDto"]; historyOffset: number; selected: Detail | null;
  draft: PolicyDraft; reviewReference: string; decisionReason: string; review: SourcePolicyCommand | null; attempt: SourcePolicyCommand | null; result: Result | null;
  references: OperationReference[]; recoveryPoolId: string; loadingPool: boolean; loadingAction: boolean; loadingHistory: boolean; loadingDetail: boolean;
  sending: boolean; recovering: boolean; fresh: boolean; expired: boolean; suspended: boolean; error: unknown; recoveryError: unknown;
};
type Options = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number; now: () => number;
  gateway: StockSourcePolicyGateway; journal: OperationJournal; newKey?: () => string };
type ReadKind = "pool" | "action" | "history" | "detail";
const invalid = () => new AccessFailure(502, "stock_source_policy_response_invalid");
const needsFactor = (operation: string) => operation === "stock.source.policy.approve" || operation === "stock.source.policy.reject";
function immutable(row: Detail) {
  return { ...row, status: "proposed", decision: null, decisionId: null, policyId: null, policyVersion: null };
}

/** Private policy views are bound to this account mount. Only original recovery references persist. */
export class StockSourcePolicyEditor {
  readonly canRead: boolean; readonly canPropose: boolean; readonly canApprove: boolean; readonly journal: OperationJournal;
  private state: Snapshot; private active = false; private generation = 0;
  private readonly reads = new Map<ReadKind, AbortController>(); private readonly listeners = new Set<() => void>();
  private readonly session: IdentitySession; private readonly ledger: MutationLedger;
  private activeReference: { operation: string; key: string } | null = null;
  constructor(private readonly options: Options) {
    sourceActor(options.actorId);
    if (!options.session.authenticated || options.session.account?.userId !== options.actorId) throw new AccessFailure(409, "actor_changed");
    this.canRead = options.actions.includes("stock.source.read"); this.canPropose = options.actions.includes("stock.source.policy.propose");
    this.canApprove = options.actions.includes("stock.source.policy.approve");
    if (!this.canRead && !this.canPropose && !this.canApprove) throw new AccessFailure(403, "access_denied");
    this.session = structuredClone(options.session); this.journal = options.journal; this.ledger = new MutationLedger(options.newKey);
    this.state = { poolInput: "", poolId: "", proposalInput: "", action: this.canApprove ? "approve" : "withdraw", preview: null, actionPreview: null,
      latest: null, history: empty(), historyOffset: 0, selected: null, draft: draft(), reviewReference: "", decisionReason: "", review: null,
      attempt: null, result: null, references: [], recoveryPoolId: "", loadingPool: false, loadingAction: false, loadingHistory: false, loadingDetail: false,
      sending: false, recovering: false, fresh: sessionTiming(this.session, options.now() - options.observedAt).fresh,
      expired: false, suspended: false, error: null, recoveryError: null };
  }
  snapshot = (): Snapshot => this.state;
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  private patch(change: Partial<Snapshot>) { this.state = { ...this.state, ...change }; this.listeners.forEach(listener => listener()); }
  isCurrent = () => this.active && !this.state.expired && !this.state.suspended &&
    !sessionTiming(this.session, this.options.now() - this.options.observedAt).expired;
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
    return { preview: null, actionPreview: null, proposalInput: "", latest: null, history: empty(), historyOffset: 0, selected: null,
      draft: draft(), reviewReference: "", decisionReason: "", review: null, result: null,
      loadingPool: false, loadingAction: false, loadingHistory: false, loadingDetail: false };
  }
  private conceal(change: Partial<Snapshot>) {
    this.generation++; this.cancelAll(); this.patch({ ...this.cleared(), poolInput: "", poolId: "", recoveryPoolId: "", attempt: null, ...change });
  }
  checkSession() {
    const timing = sessionTiming(this.session, this.options.now() - this.options.observedAt);
    if (this.active && !this.state.expired && timing.expired) this.conceal({ expired: true, fresh: false, error: new IdentityFailure(401, "session_expired") });
    else if (this.active && this.state.fresh !== timing.fresh) {
      const clearAction = !timing.fresh && this.state.action !== "withdraw";
      if (clearAction) this.cancel("action");
      this.patch({ fresh: timing.fresh, ...(clearAction ? { actionPreview: null, loadingAction: false } : {}),
        ...(!timing.fresh && this.state.review && needsFactor(this.state.review.operation) ? { review: null } : {}) });
    }
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
      if (error.code === "step_up_required") this.patch({ fresh: false, actionPreview: null, review: null });
      else if (error.status === 401 || error.status === 403 || error.code === "actor_changed" || error.code.endsWith("_response_invalid"))
        this.conceal({ suspended: true, error });
    }
  }
  private mayAct(action: SourceDecision) { return action === "withdraw" ? this.canPropose : this.canApprove; }
  private requireOperation(operation: string) {
    if (needsFactor(operation) ? !this.canApprove : !this.canPropose) throw new AccessFailure(403, "access_denied");
    if (needsFactor(operation) && !sessionTiming(this.session, this.options.now() - this.options.observedAt).fresh)
      throw new AccessFailure(403, "step_up_required");
  }
  changePool(value: string) {
    if (!this.mayEdit()) return;
    this.generation++; this.cancelAll(); this.patch({ ...this.cleared(), poolInput: value, poolId: "", error: null });
  }
  async loadPool() {
    if (!this.mayEdit()) return;
    try {
      const poolId = sourceActor(this.state.poolInput);
      this.generation++; this.cancelAll(); this.patch({ ...this.cleared(), poolId, loadingPool: true, error: null });
      const ticket = this.begin("pool");
      if (this.canPropose) {
        try {
          const value = await this.options.gateway.preview(this.options.actorId, poolId, ticket.signal);
          if (ticket.current()) {
            const preview = parsePolicyPreview(value);
            if (preview.physicalPoolId !== poolId) throw invalid();
            this.patch({ preview });
          }
        } catch (error) { if (ticket.current()) this.failed(error); }
      }
      if (ticket.current() && this.canRead) await this.loadHistory();
      if (ticket.current()) this.patch({ loadingPool: false });
      this.checkSession();
    } catch (error) { this.failed(error); }
  }
  async loadHistory(offset = 0) {
    if (!this.mayEdit() || !this.canRead || !this.state.poolId) return;
    try { stockPageRequest(offset, 25); } catch (error) { this.failed(error); return; }
    this.cancel("detail"); const poolId = this.state.poolId, ticket = this.begin("history");
    this.patch({ latest: null, history: empty(), historyOffset: offset, selected: null, loadingDetail: false, loadingHistory: true, error: null });
    try {
      const [rawPage, rawLatest] = await Promise.all([this.options.gateway.history(this.options.actorId, poolId, offset, ticket.signal),
        this.options.gateway.latest(this.options.actorId, poolId, ticket.signal)]);
      if (!ticket.current()) return;
      const history = parsePolicyHistory(rawPage, poolId, offset, 25), latest = parsePolicyLatest(rawLatest);
      if (latest.policy !== null && latest.policy.physicalPoolId !== poolId) throw invalid();
      this.patch({ history, latest });
    } catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingHistory: false }); this.checkSession(); }
  }
  async chooseHistory(id: string) {
    if (!this.mayEdit() || !this.canRead || this.state.loadingHistory) return;
    const expected = this.state.history.items.find(item => item.id === id); if (!expected) return;
    const poolId = this.state.poolId, ticket = this.begin("detail"); this.patch({ selected: null, loadingDetail: true, error: null });
    try {
      const raw = await this.options.gateway.detail(this.options.actorId, poolId, id, ticket.signal);
      if (!ticket.current()) return;
      if (raw === null) throw new AccessFailure(409, "stock_source_policy_changed");
      const selected = parseSourcePolicy(raw);
      if (stableContent(immutable(selected)) !== stableContent(immutable(expected)) ||
        expected.status !== "proposed" && stableContent(selected) !== stableContent(expected)) throw invalid();
      this.patch({ selected });
    } catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingDetail: false }); this.checkSession(); }
  }
  changeDraft(change: Partial<PolicyDraft>) { if (this.mayEdit() && this.canPropose && this.state.preview) this.patch({ draft: { ...this.state.draft, ...change }, error: null }); }
  changeProposal(proposalInput: string) {
    if (!this.mayEdit()) return; this.cancel("action");
    this.patch({ proposalInput, actionPreview: null, loadingAction: false, reviewReference: "", decisionReason: "", error: null });
  }
  changeAction(action: SourceDecision) {
    if (!this.mayEdit()) return;
    try {
      policyAction(action); if (!this.mayAct(action)) return;
      this.cancel("action"); this.patch({ action, actionPreview: null, loadingAction: false, reviewReference: "", decisionReason: "", error: null });
    } catch (error) { this.failed(error); }
  }
  async loadAction() {
    if (!this.mayEdit() || !this.state.poolId || !this.mayAct(this.state.action)) return;
    try {
      const action = this.state.action, operation = sourcePolicyOperation("stock.source.policy." + action);
      this.requireOperation(operation);
      const poolId = this.state.poolId, id = sourceActor(this.state.proposalInput), ticket = this.begin("action");
      this.patch({ actionPreview: null, loadingAction: true, reviewReference: "", decisionReason: "", error: null });
      try {
        const raw = await this.options.gateway.review(this.options.actorId, poolId, id, action, ticket.signal);
        if (!ticket.current()) return;
        this.requireOperation(operation);
        const actionPreview = parsePolicyReview(raw);
        if (actionPreview.proposal.physicalPoolId !== poolId || actionPreview.proposal.id !== id || actionPreview.action !== action) throw invalid();
        this.patch({ actionPreview });
      } catch (error) { if (ticket.current()) this.failed(error); }
      finally { if (ticket.current()) this.patch({ loadingAction: false }); this.checkSession(); }
    } catch (error) { this.failed(error); }
  }
  changeDecision(reviewReference: string, decisionReason: string) {
    if (this.mayEdit() && this.state.actionPreview) this.patch({ reviewReference, decisionReason, error: null });
  }
  reviewProposal() {
    this.refreshReferences(); if (!this.mayEdit() || !this.canPropose) return;
    try {
      if (!this.state.preview || this.state.loadingPool) throw new AccessFailure(400, "stock_source_selection_required");
      const value = this.state.draft;
      const input = { writer: value.writer, externalTenant: value.externalTenant, feedIntervalMilliseconds: value.feedIntervalMilliseconds,
        reconciliationProcedureReference: value.reconciliationProcedureReference, outageProcedureReference: value.outageProcedureReference };
      this.patch({ review: preparePolicyProposal(this.options.actorId, this.state.preview, input, value.reason), result: null, error: null });
    } catch (error) { this.failed(error); }
  }
  reviewDecision() {
    this.refreshReferences(); if (!this.mayEdit() || !this.state.actionPreview || this.state.loadingAction) return;
    try {
      this.requireOperation("stock.source.policy." + this.state.actionPreview.action);
      this.patch({ review: preparePolicyDecision(this.options.actorId, this.state.actionPreview, this.state.reviewReference, this.state.decisionReason), result: null, error: null });
    } catch (error) { this.failed(error); }
  }
  cancelReview() { if (this.guard() && !this.state.sending && !this.state.recovering) this.patch({ review: null, error: null }); }
  async submitReviewed(confirmed: boolean) {
    if (confirmed !== true || !this.guard() || !this.state.review || this.state.sending || this.state.recovering || this.state.attempt) return;
    this.refreshReferences(); if (this.state.references.length || this.state.recoveryError) return;
    const command = this.state.review;
    try { this.requireOperation(command.operation); } catch (error) { this.failed(error); return; }
    const generation = this.generation, isCurrent = () => this.isCurrent() && generation === this.generation &&
      (!needsFactor(command.operation) || sessionTiming(this.session, this.options.now() - this.options.observedAt).fresh);
    this.cancelAll(); this.patch({ sending: true, review: null, attempt: command, error: null, result: null, recoveryPoolId: command.poolId,
      loadingPool: false, loadingAction: false, loadingHistory: false, loadingDetail: false });
    try {
      const result = await submitTrackedRequest(this.ledger, this.journal, this.options.actorId, "policy", command.operation, command, async key => {
        this.activeReference = { operation: command.operation, key };
        const saved = await this.options.gateway.submit(command, key, isCurrent);
        if (!isCurrent()) throw new AccessFailure(503, "commit_outcome_unknown");
        return policyOutcome(saved, command);
      });
      if (isCurrent()) { this.activeReference = null; this.patch({ ...this.cleared(), poolInput: "", poolId: "", attempt: null, result }); }
    } catch (error) {
      if (this.active) {
        const uncertain = this.ledger.isUncertain("policy");
        this.patch({ attempt: uncertain && this.isCurrent() ? command : null, preview: null, actionPreview: null, selected: null, error });
        if (!uncertain) this.activeReference = null; this.failed(error);
      }
    } finally { if (this.active) { this.patch({ sending: false }); this.refreshReferences(); this.checkSession(); } }
  }
  setRecoveryPool(poolId: string) {
    if (!this.guard() || this.state.sending || this.state.recovering || this.state.review) return;
    this.generation++; this.cancelAll();
    this.patch({ recoveryPoolId: poolId, loadingPool: false, loadingAction: false, loadingHistory: false, loadingDetail: false, error: null });
  }
  setRecovering = (recovering: boolean) => { if (this.active) this.patch({ recovering }); };
  receipt = async (entry: OperationReference): Promise<StockSourcePolicyRecoveryReceipt | null> => {
    if (!this.guard() || entry.actorId !== this.options.actorId || !this.state.recoveryPoolId || this.state.sending || this.state.review)
      throw new AccessFailure(409, "recovery_request_stale");
    const operation = sourcePolicyOperation(entry.operation); this.requireOperation(operation);
    const poolId = sourceActor(this.state.recoveryPoolId), generation = this.generation;
    const raw = await this.options.gateway.receipt(entry, poolId);
    if (!this.guard() || generation !== this.generation) throw new AccessFailure(409, "recovery_request_stale");
    this.requireOperation(operation); if (raw === null) return null;
    const result = policyReceipt({ state: "committed", result: raw }, operation)!;
    if (result.physicalPoolId !== poolId) throw invalid();
    if (this.activeReference?.operation === entry.operation && this.activeReference.key === entry.key && this.state.attempt)
      policyOutcome(result, this.state.attempt);
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