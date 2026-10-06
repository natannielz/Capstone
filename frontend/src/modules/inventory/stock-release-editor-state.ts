import { AccessFailure, MutationLedger } from '../administration/workflow';
import { RequestNotSentError } from '../../api/client';
import { OperationJournal, submitTrackedRequest, type OperationReference } from '../administration/operation-journal';
import { IdentityFailure, parseIdentitySession, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import type { AccessScope } from '../organizations/access-scope';
import type { StockWarehouse } from './stock-count-workflow';
import type { StockReleaseGateway } from './stock-release-client';
import { sourceActor } from './stock-source-workflow';
import { releaseChoiceKey, releaseLocationKey, releaseOperation, releaseDecideOperation, prepareReleaseInput, prepareReleaseProposal, prepareReleaseDecision,
  type ReleaseChoice, type ReleasePreview, type ReleaseDetail, type ReleaseReview, type ReleaseAction, type ReleaseCommand, type ReleaseResult,
  type parseReleaseHistory } from './stock-release-workflow';

type History = ReturnType<typeof parseReleaseHistory>;
type Options = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number; now: () => number;
  gateway: StockReleaseGateway; journal: OperationJournal; newKey?: () => string };
export type ReleaseEditorState = Readonly<{ warehouses: readonly StockWarehouse[]; warehouse: StockWarehouse | null;
  sources: readonly ReleaseChoice[]; unavailableCount: string; choice: ReleaseChoice | null; packages: string; reason: string;
  preview: ReleasePreview | null; history: History; historyCursor: string | null; detail: ReleaseDetail | null; decision: ReleaseReview | null;
  decisionReason: string; review: ReleaseCommand | null; attempt: ReleaseCommand | null; result: ReleaseResult | null; references: OperationReference[];
  loadingWarehouses: boolean; loadingSources: boolean; loadingHistory: boolean; loadingDetail: boolean; loadingPreview: boolean;
  sending: boolean; recovering: boolean; expired: boolean; suspended: boolean; fresh: boolean; error: unknown; recoveryError: unknown }>;
export class StockReleaseEditor {
  readonly journal: OperationJournal;
  private state: ReleaseEditorState; private active = false; private generation = 0;
  private readonly session: IdentitySession; private readonly ledger: MutationLedger;
  private readonly reads = new Map<string, AbortController>(); private readonly listeners = new Set<() => void>();
  private original: { operation: string; key: string } | null = null;
  constructor(private readonly options: Options) {
    sourceActor(options.actorId); this.session = parseIdentitySession(options.session);
    if (!this.session.authenticated || this.session.account?.userId !== options.actorId) throw new AccessFailure(409, 'actor_changed');
    if (!options.actions.some(action => action === 'stock.release.propose' || action === 'stock.release.approve')) throw new AccessFailure(403, 'access_denied');
    this.journal = options.journal; this.ledger = new MutationLedger(options.newKey);
    this.state = { warehouses: [], warehouse: null, sources: [], unavailableCount: '0', choice: null, packages: '', reason: '', preview: null,
      history: { items: [], nextAfter: null }, historyCursor: null, detail: null, decision: null, decisionReason: '', review: null, attempt: null, result: null, references: [],
      loadingWarehouses: false, loadingSources: false, loadingHistory: false, loadingDetail: false, loadingPreview: false, sending: false, recovering: false,
      expired: false, suspended: false, fresh: sessionTiming(this.session, options.now() - options.observedAt).fresh, error: null, recoveryError: null };
  }
  snapshot = () => this.state;
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  private patch(change: Partial<ReleaseEditorState>) { this.state = { ...this.state, ...change }; this.listeners.forEach(listener => listener()); }
  get canPropose() { return this.options.actions.includes('stock.release.propose'); }
  get canApprove() { return this.options.actions.includes('stock.release.approve'); }
  isCurrent = () => this.active && !this.state.expired && !this.state.suspended && !sessionTiming(this.session, this.options.now() - this.options.observedAt).expired;
  get locked() { return !this.isCurrent() || this.state.sending || this.state.recovering || this.state.review !== null || this.state.attempt !== null ||
    this.state.references.length > 0 || this.state.recoveryError !== null; }
  private mayEdit() { this.checkSession(); return !this.locked; }
  private abort() { this.generation++; for (const controller of this.reads.values()) controller.abort(); this.reads.clear();
    this.patch({ loadingWarehouses: false, loadingSources: false, loadingHistory: false, loadingDetail: false, loadingPreview: false }); }
  private begin(kind: string, fresh = false) {
    this.reads.get(kind)?.abort(); const controller = new AbortController(); this.reads.set(kind, controller); const generation = this.generation;
    return { signal: controller.signal, current: () => this.isCurrent() && generation === this.generation && this.reads.get(kind) === controller &&
      !controller.signal.aborted && (!fresh || sessionTiming(this.session, this.options.now() - this.options.observedAt).fresh) };
  }
  private cleared() { return { sources: [], choice: null, packages: '', reason: '', preview: null, history: { items: [], nextAfter: null }, historyCursor: null,
    detail: null, decision: null, decisionReason: '', review: null, result: null, loadingSources: false, loadingHistory: false, loadingDetail: false, loadingPreview: false }; }
  private conceal(change: Partial<ReleaseEditorState>) { this.abort(); this.patch({ ...this.cleared(), warehouse: null, warehouses: [], attempt: null, loadingWarehouses: false, ...change }); }
  checkSession = () => {
    if (!this.active) return; const timing = sessionTiming(this.session, this.options.now() - this.options.observedAt);
    if (timing.expired && !this.state.expired) this.conceal({ expired: true, error: new IdentityFailure(401, 'session_expired') });
    else if (timing.fresh !== this.state.fresh) {
      if (!timing.fresh) this.reads.get('decision')?.abort();
      this.patch({ fresh: timing.fresh, ...(!timing.fresh ? { decision: null, loadingPreview: false,
        ...(this.state.review?.operation === releaseDecideOperation ? { review: null } : {}) } : {}) });
    }
  };
  dispose() { this.active = false; this.conceal({}); }
  refreshReferences = () => { try { this.patch({ references: this.journal.list(this.options.actorId), recoveryError: null }); }
    catch (error) { this.patch({ recoveryError: error }); } };
  private failed(error: unknown) {
    if (!this.active) return; this.patch({ error });
    if ((error instanceof AccessFailure || error instanceof IdentityFailure || error instanceof RequestNotSentError) &&
      (error.code === 'antiforgery_failed' || error.status === 401 || error.code === 'actor_changed' ||
      error.code === 'stock_release_response_invalid' || error.status === 403 && error.code !== 'step_up_required' && !error.code.includes('independent') && !error.code.includes('person')))
      this.conceal({ suspended: true, error });
  }
  async start() { this.active = true; this.refreshReferences(); this.checkSession(); if (this.mayEdit()) await this.loadWarehouses(); }
  async loadWarehouses() {
    if (!this.mayEdit()) return; this.abort(); this.patch({ ...this.cleared(), warehouse: null, warehouses: [], loadingWarehouses: true, error: null });
    const ticket = this.begin('warehouses');
    try { const warehouses = await this.options.gateway.warehouses(this.options.actorId, ticket.signal, ticket.current); if (ticket.current()) this.patch({ warehouses }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingWarehouses: false }); this.checkSession(); }
  }
  async chooseWarehouse(location: AccessScope | null) {
    if (!this.mayEdit() || this.state.loadingWarehouses) return;
    const warehouse = location === null ? null : this.state.warehouses.find(value => releaseLocationKey(value.location) === releaseLocationKey(location));
    if (location !== null && !warehouse) { this.failed(new AccessFailure(400, 'stock_release_selection_required')); return; }
    this.abort(); this.patch({ ...this.cleared(), warehouse: warehouse ?? null, error: null });
    if (warehouse) await this.refreshWarehouse();
  }
  async refreshWarehouse() {
    if (!this.mayEdit() || !this.state.warehouse || this.state.loadingSources || this.state.loadingHistory) return;
    const warehouse = this.state.warehouse, generation = this.generation;
    // Both reads fence the same current authority; issue them sequentially.
    await this.loadSources();
    if (this.mayEdit() && this.generation === generation && this.state.warehouse === warehouse && this.state.error === null)
      await this.loadHistory();
  }
  async loadSources() {
    if (!this.mayEdit() || !this.canPropose || !this.state.warehouse) return;
    this.patch({ sources: [], choice: null, packages: '', preview: null, loadingSources: true, error: null }); const ticket = this.begin('sources');
    try { const page = await this.options.gateway.sources(this.options.actorId, this.state.warehouse.location, ticket.signal, ticket.current);
      if (ticket.current()) this.patch({ sources: page.items, unavailableCount: page.unavailableCount }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingSources: false }); this.checkSession(); }
  }
  async loadHistory(cursor: string | null = null) {
    if (!this.mayEdit() || !this.state.warehouse) return;
    this.reads.get('detail')?.abort(); this.reads.get('decision')?.abort();
    this.patch({ history: { items: [], nextAfter: null }, detail: null, decision: null, historyCursor: cursor, loadingHistory: true, error: null }); const ticket = this.begin('history');
    try { const history = await this.options.gateway.history(this.options.actorId, this.state.warehouse.location, cursor, ticket.signal, ticket.current); if (ticket.current()) this.patch({ history }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingHistory: false }); this.checkSession(); }
  }
  chooseSource(key: string) {
    if (!this.mayEdit()) return; const choice = this.state.sources.find(item => releaseChoiceKey(item) === key) ?? null;
    this.abort(); this.patch({ choice, packages: '', preview: null, decision: null, error: null });
  }
  changePackages(packages: string) { if (this.mayEdit()) { this.abort(); this.patch({ packages, preview: null, decision: null, error: null }); } }
  changeReason(reason: string) { if (this.mayEdit()) this.patch({ reason, error: null }); }
  changeDecisionReason(decisionReason: string) { if (this.mayEdit()) this.patch({ decisionReason, error: null }); }
  async previewProposal() {
    if (!this.mayEdit() || !this.canPropose || !this.state.warehouse || !this.state.choice) return;
    const ticket = this.begin('preview'); this.patch({ loadingPreview: true, preview: null, error: null });
    try { const input = prepareReleaseInput(this.state.warehouse.location, this.state.choice, this.state.packages);
      const preview = await this.options.gateway.preview(this.options.actorId, input, ticket.signal, ticket.current); if (ticket.current()) this.patch({ preview }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingPreview: false }); this.checkSession(); }
  }
  reviewProposal() {
    if (!this.mayEdit() || !this.canPropose || !this.state.preview) return;
    try { this.patch({ review: prepareReleaseProposal(this.options.actorId, this.state.preview, this.state.reason), error: null }); }
    catch (error) { this.failed(error); }
  }
  async loadDetail(proposalId: string) {
    if (!this.mayEdit() || !this.state.history.items.some(item => item.proposalId === proposalId)) return;
    this.patch({ detail: null, decision: null, decisionReason: '', loadingDetail: true, error: null }); const ticket = this.begin('detail');
    try { const detail = await this.options.gateway.detail(this.options.actorId, proposalId, ticket.signal, ticket.current);
      if (ticket.current()) {
        if (!this.state.warehouse || releaseLocationKey(detail.proposal.input.location) !== releaseLocationKey(this.state.warehouse.location)) throw new AccessFailure(502, 'stock_release_response_invalid');
        this.patch({ detail });
      } }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingDetail: false }); this.checkSession(); }
  }
  async previewDecision(action: ReleaseAction) {
    if (!this.mayEdit() || !this.canApprove || !this.state.detail || this.state.detail.status !== 'proposed') return;
    if (!this.state.fresh) { this.failed(new AccessFailure(403, 'step_up_required')); return; }
    this.patch({ decision: null, loadingPreview: true, error: null }); const ticket = this.begin('decision', true);
    try { const decision = await this.options.gateway.review(this.options.actorId, this.state.detail, action, ticket.signal, ticket.current); if (ticket.current()) this.patch({ decision }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingPreview: false }); this.checkSession(); }
  }
  reviewDecision() {
    if (!this.mayEdit() || !this.canApprove || !this.state.fresh || !this.state.decision) return;
    try { this.patch({ review: prepareReleaseDecision(this.options.actorId, this.state.decision, this.state.decisionReason), error: null }); }
    catch (error) { this.failed(error); }
  }
  cancelReview() { if (this.isCurrent() && !this.state.sending && !this.state.attempt) this.patch({ review: null }); }
  async confirm(confirmed: boolean) {
    this.checkSession(); const command = this.state.review;
    if (!confirmed || !command || !this.isCurrent() || this.state.sending || this.state.recovering || this.state.attempt || this.state.references.length || this.state.recoveryError) return;
    if (command.operation === releaseDecideOperation && !this.state.fresh) { this.failed(new AccessFailure(403, 'step_up_required')); return; }
    const generation = this.generation, current = () => this.isCurrent() && generation === this.generation &&
      (command.operation !== releaseDecideOperation || sessionTiming(this.session, this.options.now() - this.options.observedAt).fresh);
    this.patch({ sending: true, error: null });
    try {
      const result = await submitTrackedRequest(this.ledger, this.journal, this.options.actorId, 'stock-release', command.operation, command, key => {
        this.original = { operation: command.operation, key }; return this.options.gateway.submit(command, key, current);
      });
      if (current()) this.patch({ ...this.cleared(), review: null, attempt: null, result });
    } catch (error) {
      if (this.active) { this.patch({ review: null, attempt: this.ledger.isUncertain('stock-release') && this.isCurrent() ? command : null, error }); this.failed(error); }
    } finally { if (this.active) { this.patch({ sending: false }); this.refreshReferences(); this.checkSession(); } }
  }
  setRecovering = (recovering: boolean) => { if (!this.active) return; this.patch({ recovering });
    if (!recovering && this.mayEdit() && this.state.warehouses.length === 0) void this.loadWarehouses(); };
  receipt = async (entry: OperationReference) => {
    this.checkSession(); const operation = releaseOperation(entry.operation), generation = this.generation;
    const current = () => this.isCurrent() && entry.actorId === this.options.actorId && generation === this.generation &&
      (operation !== releaseDecideOperation || sessionTiming(this.session, this.options.now() - this.options.observedAt).fresh);
    if (!current()) throw new AccessFailure(403, operation === releaseDecideOperation && !this.state.fresh ? 'step_up_required' : 'recovery_request_stale');
    const receipt = await this.options.gateway.receipt(entry, undefined, current); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return receipt;
  };
  recoveryFailed = (error: unknown) => { this.failed(error); this.refreshReferences(); };
  reconciled = () => {
    this.refreshReferences(); if (this.state.recoveryError) return;
    if (!this.original || !this.state.references.some(entry => entry.operation === this.original!.operation && entry.key === this.original!.key)) {
      this.ledger.resetAfterReconciliation(); this.original = null; this.patch({ ...this.cleared(), attempt: null, error: null });
    }
  };
}
