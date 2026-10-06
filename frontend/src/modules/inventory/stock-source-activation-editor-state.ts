import type { components } from '../../api/generated/schema';
import { AccessFailure, MutationLedger, stableContent } from '../administration/workflow';
import { OperationJournal, submitTrackedRequest, type OperationReference } from '../administration/operation-journal';
import { IdentityFailure, parseIdentitySession, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { sourceActor } from './stock-source-workflow';
import { stockPageRequest } from './stock-count-workflow';
import type { StockSourceActivationGateway } from './stock-source-activation-client';
import { activationHandoff, activationOperation, activationOutcome, activationReceipt, activationAction, parseActivationPreview, parseActivationReview,
  parseActivationDetail, parseActivationHistory, prepareActivationProposal, prepareActivationDecision,
  type ActivationAction, type ActivationPreview, type ActivationDetail, type ActivationReview, type ActivationCommand, type ActivationResult } from './stock-source-activation-workflow';

export type ActivationHandoffDraft = { reference: string; observedAtLocal: string; physicalPackages: string; carriedValueMinorUnits: string;
  emptyPoolConfirmed: boolean; previousWriterStopped: boolean; noUnrecordedStock: boolean };
const draft = (): ActivationHandoffDraft => ({ reference: '', observedAtLocal: '', physicalPackages: '', carriedValueMinorUnits: '',
  emptyPoolConfirmed: false, previousWriterStopped: false, noUnrecordedStock: false });
export function handoffFromDraft(value: ActivationHandoffDraft) {
  if (!/^\d{4}-\d\d-\d\dT\d\d:\d\d(?::\d\d)?$(?![\s\S])/.test(value.observedAtLocal)) throw new AccessFailure(400, 'stock_source_activation_input_invalid');
  const local = new Date(value.observedAtLocal), pad = (number: number) => String(number).padStart(2, '0');
  if (!Number.isFinite(local.getTime())) throw new AccessFailure(400, 'stock_source_activation_input_invalid');
  const roundTrip = local.getFullYear() + '-' + pad(local.getMonth() + 1) + '-' + pad(local.getDate()) + 'T' + pad(local.getHours()) + ':' + pad(local.getMinutes());
  if (roundTrip !== value.observedAtLocal.slice(0, 16)) throw new AccessFailure(400, 'stock_source_activation_input_invalid');
  return activationHandoff({ reference: value.reference.trim(), observedAt: local.toISOString(), physicalPackages: value.physicalPackages,
    carriedValueMinorUnits: value.carriedValueMinorUnits, emptyPoolConfirmed: value.emptyPoolConfirmed,
    previousWriterStopped: value.previousWriterStopped, noUnrecordedStock: value.noUnrecordedStock });
}
type Page = components['schemas']['StockSourceActivationPageDto'];
const empty = (): Page => ({ total: '0', items: [] });
type ReadKind = 'pool' | 'history' | 'detail' | 'action';
export type ActivationEditorState = {
  poolInput: string; poolId: string; proposalInput: string; action: ActivationAction; preview: ActivationPreview | null;
  actionPreview: ActivationReview | null; selected: ActivationDetail | null; history: Page; historyOffset: number;
  draft: ActivationHandoffDraft; reason: string; review: ActivationCommand | null; attempt: ActivationCommand | null; result: ActivationResult | null;
  references: OperationReference[]; recoveryPoolId: string; loadingPool: boolean; loadingHistory: boolean; loadingDetail: boolean; loadingAction: boolean;
  sending: boolean; recovering: boolean; fresh: boolean; expired: boolean; suspended: boolean; error: unknown; recoveryError: unknown;
};
type Options = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number; now: () => number;
  gateway: StockSourceActivationGateway; journal: OperationJournal; newKey?: () => string };
const invalid = () => new AccessFailure(502, 'stock_source_activation_response_invalid');
const needsFactor = (operation: string) => operation === 'stock.source.activation.decide';
function immutable(row: ActivationDetail) { return { ...row, status: 'proposed', decision: null, decisionId: null, activationId: null }; }

/** Only metadata recovery references persist; handoff forms and private views belong to one current account mount. */
export class StockSourceActivationEditor {
  readonly canRead: boolean; readonly canPropose: boolean; readonly canApprove: boolean; readonly journal: OperationJournal;
  private state: ActivationEditorState; private active = false; private generation = 0;
  private readonly session: IdentitySession; private readonly ledger: MutationLedger;
  private readonly reads = new Map<ReadKind, AbortController>(); private readonly listeners = new Set<() => void>();
  private activeReference: { operation: string; key: string } | null = null;
  constructor(private readonly options: Options) {
    sourceActor(options.actorId);
    this.session = parseIdentitySession(options.session);
    if (!this.session.authenticated || this.session.account?.userId !== options.actorId) throw new AccessFailure(409, 'actor_changed');
    this.canRead = options.actions.includes('stock.source.read'); this.canPropose = options.actions.includes('stock.source.propose');
    this.canApprove = options.actions.includes('stock.source.approve');
    if (!this.canRead && !this.canPropose && !this.canApprove) throw new AccessFailure(403, 'access_denied');
    this.journal = options.journal; this.ledger = new MutationLedger(options.newKey);
    this.state = { poolInput: '', poolId: '', proposalInput: '', action: 'approve', preview: null, actionPreview: null, selected: null,
      history: empty(), historyOffset: 0, draft: draft(), reason: '', review: null, attempt: null, result: null, references: [], recoveryPoolId: '',
      loadingPool: false, loadingHistory: false, loadingDetail: false, loadingAction: false, sending: false, recovering: false,
      fresh: sessionTiming(this.session, options.now() - options.observedAt).fresh, expired: false, suspended: false, error: null, recoveryError: null };
  }
  snapshot = (): ActivationEditorState => this.state;
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  private patch(change: Partial<ActivationEditorState>) { this.state = { ...this.state, ...change }; this.listeners.forEach(listener => listener()); }
  isCurrent = () => this.active && !this.state.expired && !this.state.suspended && !sessionTiming(this.session, this.options.now() - this.options.observedAt).expired;
  private guard() { this.checkSession(); return this.isCurrent(); }
  get locked() { return !this.isCurrent() || this.state.sending || this.state.recovering || this.state.review !== null || this.state.attempt !== null ||
    this.state.references.length > 0 || this.state.recoveryError !== null; }
  private mayEdit() { return this.guard() && !this.locked; }
  private cancel(kind: ReadKind) { this.reads.get(kind)?.abort(); this.reads.delete(kind); }
  private cancelAll() { for (const kind of this.reads.keys()) this.cancel(kind); }
  private begin(kind: ReadKind) { this.cancel(kind); const controller = new AbortController(); this.reads.set(kind, controller);
    return { signal: controller.signal, current: () => this.isCurrent() && this.reads.get(kind) === controller && !controller.signal.aborted }; }
  private cleared() { return { preview: null, actionPreview: null, selected: null, history: empty(), historyOffset: 0, draft: draft(), reason: '',
    proposalInput: '', review: null, result: null, loadingPool: false, loadingHistory: false, loadingDetail: false, loadingAction: false }; }
  private conceal(change: Partial<ActivationEditorState>) { this.generation++; this.cancelAll(); this.patch({ ...this.cleared(), poolInput: '', poolId: '', attempt: null, ...change }); }
  checkSession() {
    if (!this.active) return; const timing = sessionTiming(this.session, this.options.now() - this.options.observedAt);
    if (timing.expired && !this.state.expired) this.conceal({ expired: true, error: new IdentityFailure(401, 'session_expired') });
    else if (this.state.fresh !== timing.fresh) {
      if (!timing.fresh) this.cancel('action');
      this.patch({ fresh: timing.fresh, ...(!timing.fresh ? { actionPreview: null, loadingAction: false } : {}),
        ...(!timing.fresh && this.state.review && needsFactor(this.state.review.operation) ? { review: null } : {}) });
    }
  }
  async start() { if (this.active) return; this.active = true; this.refreshReferences(); this.checkSession(); }
  dispose() { this.active = false; this.conceal({}); }
  refreshReferences = () => { try { this.patch({ references: this.journal.list(this.options.actorId), recoveryError: null }); }
    catch (error) { this.patch({ recoveryError: error }); } };
  private failed(error: unknown) {
    if (!this.active) return; this.patch({ error });
    if (error instanceof AccessFailure || error instanceof IdentityFailure) {
      if (error.code === 'step_up_required') this.patch({ fresh: false, actionPreview: null, review: null });
      else if (error.status === 401 || error.status === 403 || error.code === 'actor_changed' || error.code.endsWith('_response_invalid')) this.conceal({ suspended: true, error });
    }
  }
  private requireOperation(operation: string) {
    if (needsFactor(operation) ? !this.canApprove : !this.canPropose) throw new AccessFailure(403, 'access_denied');
    if (needsFactor(operation) && !sessionTiming(this.session, this.options.now() - this.options.observedAt).fresh) throw new AccessFailure(403, 'step_up_required');
  }
  changePool(value: string) { if (!this.mayEdit()) return; this.generation++; this.cancelAll(); this.patch({ ...this.cleared(), poolInput: value, poolId: '', error: null }); }
  async loadPool() {
    if (!this.mayEdit()) return;
    try {
      const poolId = sourceActor(this.state.poolInput); this.generation++; this.cancelAll(); this.patch({ ...this.cleared(), poolId, loadingPool: true, error: null });
      const ticket = this.begin('pool');
      if (this.canPropose) {
        try { const preview = parseActivationPreview(await this.options.gateway.preview(this.options.actorId, poolId, ticket.signal));
          if (ticket.current()) { if (preview.physicalPoolId !== poolId) throw invalid(); this.patch({ preview }); } }
        catch (error) { if (ticket.current()) this.failed(error); }
      }
      if (ticket.current() && this.canRead) await this.loadHistory();
      if (ticket.current()) this.patch({ loadingPool: false }); this.checkSession();
    } catch (error) { this.failed(error); }
  }
  async loadHistory(offset = 0) {
    if (!this.mayEdit() || !this.canRead || !this.state.poolId) return;
    try { stockPageRequest(offset, 25); } catch (error) { this.failed(error); return; }
    this.cancel('detail'); const poolId = this.state.poolId, ticket = this.begin('history');
    this.patch({ history: empty(), historyOffset: offset, selected: null, loadingHistory: true, loadingDetail: false, error: null });
    try { const raw = await this.options.gateway.history(this.options.actorId, poolId, offset, ticket.signal);
      if (ticket.current()) this.patch({ history: parseActivationHistory(raw, poolId, offset, 25) }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingHistory: false }); this.checkSession(); }
  }
  async chooseHistory(id: string) {
    if (!this.mayEdit() || !this.canRead || this.state.loadingHistory) return;
    const expected = this.state.history.items.find(item => item.id === id); if (!expected) return;
    const poolId = this.state.poolId, ticket = this.begin('detail'); this.patch({ selected: null, loadingDetail: true, error: null });
    try {
      const raw = await this.options.gateway.detail(this.options.actorId, poolId, id, ticket.signal); if (!ticket.current()) return;
      if (raw === null) throw new AccessFailure(409, 'stock_source_activation_changed'); const selected = parseActivationDetail(raw);
      if (stableContent(immutable(selected)) !== stableContent(immutable(expected)) || expected.status !== 'proposed' && stableContent(selected) !== stableContent(expected)) throw invalid();
      this.patch({ selected, proposalInput: selected.id, actionPreview: null });
    } catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingDetail: false }); this.checkSession(); }
  }
  changeDraft(change: Partial<ActivationHandoffDraft>) { if (this.mayEdit()) { this.cancel('action'); this.patch({ draft: { ...this.state.draft, ...change }, actionPreview: null, loadingAction: false, error: null }); } }
  changeReason(reason: string) { if (this.mayEdit()) this.patch({ reason, error: null }); }
  changeProposal(proposalInput: string) { if (this.mayEdit()) { this.cancel('action'); this.patch({ proposalInput, actionPreview: null, loadingAction: false, error: null }); } }
  changeAction(value: ActivationAction) { if (this.mayEdit()) { this.cancel('action'); this.patch({ action: activationAction(value), actionPreview: null, loadingAction: false, error: null }); } }
  async loadAction() {
    if (!this.mayEdit() || !this.canApprove || !this.state.poolId) return;
    try {
      this.requireOperation('stock.source.activation.decide'); const id = sourceActor(this.state.proposalInput), poolId = this.state.poolId;
      const action = this.state.action, handoff = action === 'approve' ? handoffFromDraft(this.state.draft) : null, ticket = this.begin('action');
      this.patch({ actionPreview: null, loadingAction: true, error: null });
      try {
        const raw = await this.options.gateway.review(this.options.actorId, poolId, id, action, handoff, ticket.signal); if (!ticket.current()) return;
        this.requireOperation('stock.source.activation.decide'); const actionPreview = parseActivationReview(raw);
        if (actionPreview.physicalPoolId !== poolId || actionPreview.proposalId !== id || actionPreview.action !== action ||
          stableContent(actionPreview.handoff) !== stableContent(handoff)) throw invalid(); this.patch({ actionPreview });
      } catch (error) { if (ticket.current()) this.failed(error); }
      finally { if (ticket.current()) this.patch({ loadingAction: false }); this.checkSession(); }
    } catch (error) { this.failed(error); }
  }
  reviewProposal() { this.refreshReferences(); if (!this.mayEdit() || !this.canPropose) return;
    try { if (!this.state.preview || this.state.loadingPool) throw new AccessFailure(400, 'stock_source_selection_required');
      this.patch({ review: prepareActivationProposal(this.options.actorId, this.state.preview, handoffFromDraft(this.state.draft), this.state.reason), result: null, error: null }); }
    catch (error) { this.failed(error); } }
  reviewDecision() { this.refreshReferences(); if (!this.mayEdit() || !this.state.actionPreview || this.state.loadingAction) return;
    try { this.requireOperation('stock.source.activation.decide'); this.patch({ review: prepareActivationDecision(this.options.actorId, this.state.actionPreview, this.state.reason), result: null, error: null }); }
    catch (error) { this.failed(error); } }
  cancelReview() { if (this.guard() && !this.state.sending && !this.state.recovering) this.patch({ review: null, error: null }); }
  async submitReviewed(confirmed: boolean) {
    if (confirmed !== true || !this.guard() || !this.state.review || this.state.sending || this.state.recovering || this.state.attempt) return;
    this.refreshReferences(); if (this.state.references.length || this.state.recoveryError) return;
    const command = this.state.review; try { this.requireOperation(command.operation); } catch (error) { this.failed(error); return; }
    const generation = this.generation, current = () => this.isCurrent() && generation === this.generation &&
      (!needsFactor(command.operation) || sessionTiming(this.session, this.options.now() - this.options.observedAt).fresh);
    this.cancelAll(); this.patch({ sending: true, review: null, attempt: command, result: null, error: null, recoveryPoolId: command.poolId,
      loadingPool: false, loadingHistory: false, loadingDetail: false, loadingAction: false });
    try {
      const result = await submitTrackedRequest(this.ledger, this.journal, this.options.actorId, 'activation', command.operation, command, async key => {
        this.activeReference = { operation: command.operation, key }; const saved = await this.options.gateway.submit(command, key, current);
        if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); return activationOutcome(saved, command);
      });
      if (current()) { this.activeReference = null; this.patch({ ...this.cleared(), poolInput: '', poolId: '', attempt: null, result }); }
    } catch (error) { if (this.active) { const uncertain = this.ledger.isUncertain('activation');
      this.patch({ attempt: uncertain && this.isCurrent() ? command : null, preview: null, actionPreview: null, selected: null, error });
      if (!uncertain) this.activeReference = null; this.failed(error); } }
    finally { if (this.active) { this.patch({ sending: false }); this.refreshReferences(); this.checkSession(); } }
  }
  setRecoveryPool(value: string) { if (!this.guard() || this.state.sending || this.state.recovering || this.state.review) return;
    this.generation++; this.cancelAll(); this.patch({ recoveryPoolId: value, loadingPool: false, loadingHistory: false, loadingDetail: false, loadingAction: false, error: null }); }
  setRecovering = (recovering: boolean) => { if (this.active) this.patch({ recovering }); };
  receipt = async (entry: OperationReference): Promise<{ state: 'committed'; result: ActivationResult } | null> => {
    if (!this.guard() || entry.actorId !== this.options.actorId || !this.state.recoveryPoolId || this.state.sending || this.state.review) throw new AccessFailure(409, 'recovery_request_stale');
    const operation = activationOperation(entry.operation); this.requireOperation(operation); const poolId = sourceActor(this.state.recoveryPoolId), generation = this.generation;
    const raw = await this.options.gateway.receipt(entry, poolId); if (!this.guard() || generation !== this.generation) throw new AccessFailure(409, 'recovery_request_stale');
    this.requireOperation(operation); if (raw === null) return null; const result = activationReceipt({ state: 'committed', result: raw }, operation)!;
    if (result.physicalPoolId !== poolId) throw invalid();
    if (this.activeReference?.operation === entry.operation && this.activeReference.key === entry.key && this.state.attempt) activationOutcome(result, this.state.attempt);
    return { state: 'committed', result };
  };
  recoveryFailed = (error: unknown) => { this.failed(error); this.refreshReferences(); };
  reconciled = () => { this.refreshReferences(); if (this.state.recoveryError) return; const pending = this.activeReference;
    if (!pending || !this.state.references.some(entry => entry.operation === pending.operation && entry.key === pending.key)) {
      this.ledger.resetAfterReconciliation(); this.activeReference = null; this.patch({ ...this.cleared(), poolInput: '', poolId: '', attempt: null, recoveryPoolId: '', error: null });
    } };
}
