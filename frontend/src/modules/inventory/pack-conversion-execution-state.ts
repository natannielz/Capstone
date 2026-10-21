import { AccessFailure, MutationLedger, businessRefusal } from '../administration/workflow';
import { OperationJournal, submitTrackedRequest, type OperationReference } from '../administration/operation-journal';
import { IdentityFailure, parseIdentitySession, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import { stockLocationKey, type StockWarehouse } from './stock-count-workflow';
import { type PackRule } from './pack-conversion-workflow';
import { sourceActor } from './stock-source-workflow';
import { prepareConversionInput, prepareConversion, conversionOperation, conversionOutcome, type ConversionCommand, type ConversionPreview,
  type ConversionSource, type ConversionDetail, type ConversionResult } from './pack-conversion-execution-workflow';
import type { PackExecutionGateway } from './pack-conversion-execution-client';

type Page<T> = Readonly<{ items: readonly T[]; next: string | null }>;
type ReadKind = 'warehouses' | 'rules' | 'sources' | 'preview' | 'history';
type Options = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number; now: () => number;
  gateway: PackExecutionGateway; journal: OperationJournal; newKey?: () => string };
export type ConversionState = Readonly<{
  warehouses: readonly StockWarehouse[]; warehouse: StockWarehouse | null; rules: Page<PackRule>; ruleCursor: string | null; rule: PackRule | null;
  sources: Page<ConversionSource> & { unavailableCount: string }; sourceCursor: string | null; source: ConversionSource | null;
  steps: string; reason: string; preview: ConversionPreview | null; review: ConversionCommand | null; attempt: ConversionCommand | null; result: ConversionResult | null;
  history: Page<ConversionDetail>; historyCursor: string | null; references: OperationReference[]; error: unknown; recoveryError: unknown;
  loadingWarehouses: boolean; loadingRules: boolean; loadingSources: boolean; loadingPreview: boolean; loadingHistory: boolean;
  sending: boolean; recovering: boolean; expired: boolean; suspended: boolean;
}>;
const empty = <T>(): Page<T> => ({ items: [], next: null });
/** The existing journal retains only original-key metadata; the frozen execution stays in memory. */
export class PackExecutionEditor {
  readonly journal: OperationJournal;
  private state: ConversionState;
  private active = false; private generation = 0;
  private readonly session: IdentitySession; private readonly ledger: MutationLedger;
  private readonly reads = new Map<ReadKind, AbortController>(); private readonly listeners = new Set<() => void>();
  private activeReference: { operation: string; key: string } | null = null;
  constructor(private readonly options: Options) {
    sourceActor(options.actorId);
    if (!options.session.authenticated || options.session.account?.userId !== options.actorId) throw new AccessFailure(409, 'actor_changed');
    if (!options.actions.includes('stock.convert')) throw new AccessFailure(403, 'access_denied');
    this.session = parseIdentitySession(options.session); this.journal = options.journal; this.ledger = new MutationLedger(options.newKey);
    this.state = { warehouses: [], warehouse: null, ...this.cleared(), references: [], recoveryError: null, loadingWarehouses: false,
      sending: false, recovering: false, expired: false, suspended: false, error: null, attempt: null };
  }
  snapshot = () => this.state;
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  private patch(change: Partial<ConversionState>) { this.state = { ...this.state, ...change }; this.listeners.forEach(listener => listener()); }
  isCurrent = () => this.active && !this.state.expired && !this.state.suspended && !sessionTiming(this.session, this.options.now() - this.options.observedAt).expired;
  private guard() { this.checkSession(); return this.isCurrent(); }
  get locked() { return !this.isCurrent() || this.state.sending || this.state.recovering || this.state.review !== null || this.state.attempt !== null || this.state.references.length > 0 || this.state.recoveryError !== null; }
  private mayEdit() { return this.guard() && !this.locked; }
  private cancel(kind: ReadKind) { this.reads.get(kind)?.abort(); this.reads.delete(kind); }
  private cancelAll() { for (const kind of this.reads.keys()) this.cancel(kind); }
  private begin(kind: ReadKind) {
    this.cancel(kind); const controller = new AbortController(); this.reads.set(kind, controller);
    return { signal: controller.signal, current: () => this.isCurrent() && this.reads.get(kind) === controller && !controller.signal.aborted };
  }
  private cleared() {
    return { rules: empty<PackRule>(), ruleCursor: null, rule: null, sources: { ...empty<ConversionSource>(), unavailableCount: '0' }, sourceCursor: null, source: null,
      steps: '', reason: '', preview: null, review: null, result: null, history: empty<ConversionDetail>(), historyCursor: null,
      loadingRules: false, loadingSources: false, loadingPreview: false, loadingHistory: false };
  }
  private conceal(change: Partial<ConversionState>) {
    this.generation++; this.cancelAll(); this.patch({ ...this.cleared(), warehouses: [], warehouse: null, attempt: null, loadingWarehouses: false, ...change });
  }
  checkSession() { if (this.active && !this.state.expired && sessionTiming(this.session, this.options.now() - this.options.observedAt).expired)
    this.conceal({ expired: true, error: new IdentityFailure(401, 'session_expired') }); }
  dispose() { this.active = false; this.conceal({}); }
  refreshReferences = () => { try { this.patch({ references: this.journal.list(this.options.actorId), recoveryError: null }); }
    catch (error) { this.patch({ recoveryError: error }); } };
  private failed(error: unknown) {
    if (!this.active) return; this.patch({ error });
    if ((error instanceof AccessFailure || error instanceof IdentityFailure) &&
      (error.status === 401 || error.status === 403 && !businessRefusal(error.code) || error.code === 'actor_changed' || error.code === 'pack_execution_response_invalid')) this.conceal({ suspended: true, error });
  }
  async start() { if (this.active) return; this.active = true; this.refreshReferences(); if (this.guard()) await this.loadWarehouses(); }
  async loadWarehouses() {
    if (!this.mayEdit()) return; this.generation++; this.cancelAll();
    this.patch({ ...this.cleared(), result: this.state.result, warehouses: [], warehouse: null, loadingWarehouses: true, error: null }); const ticket = this.begin('warehouses');
    try { const warehouses = await this.options.gateway.warehouses(this.options.actorId, ticket.signal, ticket.current); if (ticket.current()) this.patch({ warehouses }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingWarehouses: false }); this.checkSession(); }
  }
  async chooseWarehouse(key: string) {
    if (!this.mayEdit() || this.state.loadingWarehouses) return;
    const warehouse = key === '' ? null : this.state.warehouses.find(item => stockLocationKey(item.location) === key);
    if (key && !warehouse) { this.failed(new AccessFailure(400, 'pack_execution_selection_required')); return; }
    this.generation++; this.cancelAll(); this.patch({ ...this.cleared(), warehouse: warehouse ?? null, error: null });
    if (warehouse) await Promise.all([this.loadRules(), this.loadHistory()]);
  }
  private invalidate() { this.generation++; this.cancel('preview'); this.patch({ preview: null, review: null, result: null, loadingPreview: false, error: null }); }
  async loadRules(cursor: string | null = null) {
    if (!this.mayEdit() || !this.state.warehouse) return;
    this.invalidate(); this.cancel('sources'); this.patch({ rules: empty(), ruleCursor: cursor, rule: null,
      sources: { ...empty(), unavailableCount: '0' }, sourceCursor: null, source: null, steps: '', loadingSources: false, loadingRules: true });
    const ticket = this.begin('rules');
    try { const rules = await this.options.gateway.rules(this.options.actorId, this.state.warehouse.location, cursor, ticket.signal, ticket.current); if (ticket.current()) this.patch({ rules }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingRules: false }); this.checkSession(); }
  }
  async chooseRule(id: string) {
    if (!this.mayEdit() || this.state.loadingRules) return;
    const rule = id === '' ? null : this.state.rules.items.find(item => item.id === id);
    if (id && !rule) { this.failed(new AccessFailure(400, 'pack_execution_selection_required')); return; }
    this.invalidate(); this.cancel('sources'); this.patch({ rule: rule ?? null, sources: { ...empty(), unavailableCount: '0' }, sourceCursor: null,
      source: null, steps: '', loadingSources: false }); if (rule) await this.loadSources();
  }
  async loadSources(cursor: string | null = null) {
    if (!this.mayEdit() || !this.state.warehouse || !this.state.rule) return;
    this.invalidate(); this.patch({ sources: { ...empty(), unavailableCount: '0' }, sourceCursor: cursor, source: null, steps: '', loadingSources: true });
    const ticket = this.begin('sources');
    try { const sources = await this.options.gateway.sources(this.options.actorId, this.state.warehouse.location, this.state.rule, cursor, ticket.signal, ticket.current); if (ticket.current()) this.patch({ sources }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingSources: false }); this.checkSession(); }
  }
  chooseSource(id: string) {
    if (!this.mayEdit() || this.state.loadingSources) return;
    const source = id === '' ? null : this.state.sources.items.find(item => item.sourceBalanceId === id && item.maximumSteps !== '0');
    if (id && !source) { this.failed(new AccessFailure(400, 'pack_execution_selection_required')); return; }
    this.invalidate(); this.patch({ source: source ?? null, steps: '' });
  }
  change(field: 'steps' | 'reason', value: string) { if (!this.mayEdit()) return; this.invalidate(); this.patch({ [field]: value }); }
  async preview() {
    if (!this.mayEdit()) return;
    try {
      if (!this.state.warehouse || !this.state.rule || !this.state.source || this.state.loadingSources || this.state.loadingRules)
        throw new AccessFailure(400, 'pack_execution_selection_required');
      const input = prepareConversionInput(this.state.warehouse.location, this.state.rule, this.state.source, this.state.steps);
      this.invalidate(); const ticket = this.begin('preview'); this.patch({ loadingPreview: true });
      try { const preview = await this.options.gateway.preview(this.options.actorId, input, this.state.rule, this.state.source, ticket.signal, ticket.current);
        if (ticket.current()) this.patch({ preview }); }
      catch (error) { if (ticket.current()) this.failed(error); }
      finally { if (ticket.current()) this.patch({ loadingPreview: false }); this.checkSession(); }
    } catch (error) { this.failed(error); }
  }
  review() {
    this.refreshReferences(); if (!this.mayEdit()) return;
    try { if (!this.state.preview || this.state.loadingPreview) throw new AccessFailure(400, 'pack_execution_preview_required');
      this.patch({ review: prepareConversion(this.options.actorId, this.state.preview, this.state.reason), error: null }); }
    catch (error) { this.failed(error); }
  }
  cancelReview() { if (this.guard() && !this.state.sending && !this.state.recovering) this.patch({ review: null, error: null }); }
  async execute(confirmed: boolean) {
    if (!confirmed || !this.state.review || !this.guard() || this.state.sending || this.state.recovering || this.state.attempt) return;
    this.refreshReferences(); if (this.state.references.length || this.state.recoveryError) return;
    const command = this.state.review, generation = this.generation, current = () => this.isCurrent() && generation === this.generation;
    this.cancelAll(); this.patch({ sending: true, review: null, attempt: command, result: null, error: null,
      loadingWarehouses: false, loadingRules: false, loadingSources: false, loadingPreview: false, loadingHistory: false });
    try {
      const result = await submitTrackedRequest(this.ledger, this.journal, this.options.actorId, 'pack-execution', conversionOperation, command, async key => {
        this.activeReference = { operation: conversionOperation, key }; const result = await this.options.gateway.submit(command, key, current);
        if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); return conversionOutcome(result, command);
      });
      if (current()) { this.activeReference = null; this.patch({ preview: null, source: null, steps: '', reason: '', attempt: null, result,
        sources: { ...empty(), unavailableCount: '0' }, sourceCursor: null, history: empty(), historyCursor: null }); }
    } catch (error) {
      if (this.active) { const uncertain = this.ledger.isUncertain('pack-execution');
        this.patch({ attempt: this.isCurrent() && uncertain ? command : null, preview: null }); if (!uncertain) this.activeReference = null; this.failed(error); }
    } finally {
      if (this.active) { this.patch({ sending: false }); this.refreshReferences(); this.checkSession(); }
    }
  }
  async loadHistory(cursor: string | null = null) {
    if (!this.mayEdit() || !this.state.warehouse) return;
    const ticket = this.begin('history'); this.patch({ history: empty(), historyCursor: cursor, loadingHistory: true, error: null });
    try { const history = await this.options.gateway.history(this.options.actorId, this.state.warehouse.location, cursor, ticket.signal, ticket.current); if (ticket.current()) this.patch({ history }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingHistory: false }); this.checkSession(); }
  }
  setRecovering = (recovering: boolean) => {
    if (!this.active) return; this.patch({ recovering });
    if (!recovering && this.mayEdit() && !this.state.warehouses.length && !this.state.loadingWarehouses) void this.loadWarehouses();
  };
  receipt = async (entry: OperationReference) => {
    const generation = this.generation, current = () => this.isCurrent() && generation === this.generation && entry.actorId === this.options.actorId;
    if (!current()) throw new AccessFailure(409, 'recovery_request_stale');
    const receipt = await this.options.gateway.receipt(entry, this.options.actorId, undefined, current);
    if (!current()) throw new AccessFailure(409, 'recovery_request_stale');
    if (receipt) {
      const result = this.state.attempt && this.activeReference?.key === entry.key ? conversionOutcome(receipt.result, this.state.attempt) : receipt.result;
      this.patch({ result });
    }
    return receipt;
  };
  recoveryFailed = (error: unknown) => { this.failed(error); this.refreshReferences(); };
  reconciled = () => {
    this.refreshReferences(); if (this.state.recoveryError) return;
    if (!this.activeReference || !this.state.references.some(entry => entry.operation === this.activeReference!.operation && entry.key === this.activeReference!.key)) {
      this.ledger.resetAfterReconciliation(); this.activeReference = null;
      this.patch({ attempt: null, preview: null, review: null, source: null, steps: '', reason: '', sources: { ...empty(), unavailableCount: '0' }, sourceCursor: null, history: empty(), error: null });
      if (this.mayEdit() && !this.state.warehouses.length) void this.loadWarehouses();
    }
  };
}
