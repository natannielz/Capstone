import { AccessFailure, MutationLedger, stableContent, businessRefusal } from '../administration/workflow';
import { OperationJournal, submitTrackedRequest, type OperationReference } from '../administration/operation-journal';
import { IdentityFailure, parseIdentitySession, sessionTiming, type IdentitySession } from '../identity/identity-workflow';
import type { AccessScope } from '../organizations/access-scope';
import { stockLocationKey, stockPageRequest, type StockWarehouse, type StockPackageChoice } from './stock-count-workflow';
import type { PackConversionGateway } from './pack-conversion-client';
import { preparePackInput, parsePackPreview, parsePackProposal, parsePackDecisionPreview, preparePackProposal, preparePackDecision,
  packOutcome, packActor, packOperation,
  type PackRole, type PackAction, type PackPreview, type PackProposal, type PackDecisionPreview, type PackRule, type PackCommand, type PackResult } from './pack-conversion-workflow';

type Page<T> = Readonly<{ total: string; items: readonly T[] }>;
type Cursor<T> = Readonly<{ items: readonly T[]; next: string | null }>;
type Mode = 'prepare' | 'proposals' | 'rules';
type ReadKind = 'warehouses' | 'source' | 'destination' | 'preview' | 'proposals' | 'detail' | 'decision' | 'rules';
export type PackEditorState = Readonly<{
  mode: Mode; warehouses: StockWarehouse[]; warehouse: StockWarehouse | null;
  sourcePage: Page<StockPackageChoice>; destinationPage: Page<StockPackageChoice>; sourceOffset: number; destinationOffset: number;
  sourceSearch: string; destinationSearch: string; source: StockPackageChoice | null; destination: StockPackageChoice | null;
  sourceRatio: string; destinationRatio: string; reason: string; preview: PackPreview | null;
  proposals: Cursor<PackProposal>; proposalCursor: string | null; selected: PackProposal | null;
  decisionPreview: PackDecisionPreview | null; rules: Cursor<PackRule>; ruleCursor: string | null;
  review: PackCommand | null; attempt: PackCommand | null; result: PackResult | null; references: OperationReference[];
  loadingWarehouses: boolean; loadingSource: boolean; loadingDestination: boolean; loadingPreview: boolean; loadingProposals: boolean;
  loadingDetail: boolean; loadingDecision: boolean; loadingRules: boolean; sending: boolean; recovering: boolean;
  expired: boolean; suspended: boolean; error: unknown; recoveryError: unknown;
}>;
type Options = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number; now: () => number;
  gateway: PackConversionGateway; journal: OperationJournal; newKey?: () => string };
const page = <T>(): Page<T> => ({ total: '0', items: [] });
const cursor = <T>(): Cursor<T> => ({ items: [], next: null });
/** Ordinary MFA permission; the server owns actual person independence and current authorization. */
export class PackConversionEditor {
  readonly journal: OperationJournal;
  private state: PackEditorState;
  private active = false; private generation = 0;
  private readonly session: IdentitySession; private readonly ledger: MutationLedger;
  private readonly reads = new Map<ReadKind, AbortController>(); private readonly listeners = new Set<() => void>();
  private activeReference: { operation: string; key: string } | null = null;
  constructor(private readonly options: Options) {
    packActor(options.actorId);
    if (!options.session.authenticated || options.session.account?.userId !== options.actorId) throw new AccessFailure(409, 'actor_changed');
    if (!options.actions.includes('stock.convert')) throw new AccessFailure(403, 'access_denied');
    this.session = parseIdentitySession(options.session); this.journal = options.journal; this.ledger = new MutationLedger(options.newKey);
    this.state = { mode: 'prepare', warehouses: [], warehouse: null, sourcePage: page(), destinationPage: page(), sourceOffset: 0, destinationOffset: 0,
      sourceSearch: '', destinationSearch: '', source: null, destination: null, sourceRatio: '', destinationRatio: '', reason: '', preview: null,
      proposals: cursor(), proposalCursor: null, selected: null, decisionPreview: null, rules: cursor(), ruleCursor: null,
      review: null, attempt: null, result: null, references: [], loadingWarehouses: false, loadingSource: false, loadingDestination: false,
      loadingPreview: false, loadingProposals: false, loadingDetail: false, loadingDecision: false, loadingRules: false,
      sending: false, recovering: false, expired: false, suspended: false, error: null, recoveryError: null };
  }
  snapshot = (): PackEditorState => this.state;
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  private patch(change: Partial<PackEditorState>) { this.state = { ...this.state, ...change }; this.listeners.forEach(listener => listener()); }
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
    return { sourcePage: page<StockPackageChoice>(), destinationPage: page<StockPackageChoice>(), sourceOffset: 0, destinationOffset: 0,
      sourceSearch: '', destinationSearch: '', source: null, destination: null, sourceRatio: '', destinationRatio: '', reason: '', preview: null,
      proposals: cursor<PackProposal>(), proposalCursor: null, selected: null, decisionPreview: null, rules: cursor<PackRule>(), ruleCursor: null,
      review: null, result: null, loadingSource: false, loadingDestination: false, loadingPreview: false, loadingProposals: false,
      loadingDetail: false, loadingDecision: false, loadingRules: false };
  }
  private conceal(change: Partial<PackEditorState>) {
    this.generation++; this.cancelAll(); this.patch({ ...this.cleared(), warehouses: [], warehouse: null, loadingWarehouses: false, attempt: null, ...change });
  }
  checkSession() { if (this.active && !this.state.expired && sessionTiming(this.session, this.options.now() - this.options.observedAt).expired)
    this.conceal({ expired: true, error: new IdentityFailure(401, 'session_expired') }); }
  dispose() { this.active = false; this.conceal({}); }
  refreshReferences = () => { try { this.patch({ references: this.journal.list(this.options.actorId), recoveryError: null }); }
    catch (error) { this.patch({ recoveryError: error }); } };
  private failed(error: unknown) {
    if (!this.active) return; this.patch({ error });
    if (error instanceof AccessFailure || error instanceof IdentityFailure) {
      if (error.status === 401 || error.code === 'actor_changed' || error.status === 403 && !businessRefusal(error.code) ||
        error.code === 'pack_conversion_response_invalid') this.conceal({ suspended: true, error });
    }
  }
  async start() { if (this.active) return; this.active = true; this.refreshReferences(); if (this.guard()) await this.loadWarehouses(); }
  async loadWarehouses() {
    if (!this.mayEdit()) return; this.generation++; this.cancelAll();
    this.patch({ ...this.cleared(), warehouse: null, warehouses: [], loadingWarehouses: true, error: null }); const ticket = this.begin('warehouses');
    try { const warehouses = await this.options.gateway.warehouses(this.options.actorId, ticket.signal, ticket.current); if (ticket.current()) this.patch({ warehouses }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingWarehouses: false }); this.checkSession(); }
  }
  async chooseWarehouse(location: AccessScope | null) {
    if (!this.mayEdit() || this.state.loadingWarehouses) return;
    const warehouse = location === null ? null : this.state.warehouses.find(item => stockLocationKey(item.location) === stockLocationKey(location));
    if (location !== null && !warehouse) { this.failed(new AccessFailure(400, 'pack_conversion_selection_required')); return; }
    this.generation++; this.cancelAll(); this.patch({ ...this.cleared(), warehouse: warehouse ?? null, error: null });
    if (warehouse) await this.loadMode();
  }
  async changeMode(mode: Mode) {
    if (!this.mayEdit() || !['prepare', 'proposals', 'rules'].includes(mode)) return;
    this.generation++; this.cancelAll(); this.patch({ ...this.cleared(), mode, error: null }); if (this.state.warehouse) await this.loadMode();
  }
  private async loadMode() {
    if (this.state.mode === 'prepare') await Promise.all([this.loadPackages('source'), this.loadPackages('destination')]);
    else if (this.state.mode === 'proposals') await this.loadProposals(); else await this.loadRules();
  }
  private invalidatePreview() { this.generation++; this.cancel('preview'); this.cancel('decision'); this.patch({ preview: null, decisionPreview: null, review: null, result: null, loadingPreview: false, loadingDecision: false }); }
  changeSearch(role: PackRole, search: string) {
    if (!this.mayEdit() || this.state.mode !== 'prepare') return; this.cancel(role); this.invalidatePreview();
    this.patch(role === 'source' ? { sourceSearch: search, sourcePage: page(), source: null, sourceOffset: 0, loadingSource: false }
      : { destinationSearch: search, destinationPage: page(), destination: null, destinationOffset: 0, loadingDestination: false });
  }
  async loadPackages(role: PackRole, offset = 0) {
    if (!this.mayEdit() || !this.state.warehouse || this.state.mode !== 'prepare') return;
    try { stockPageRequest(offset, 25); } catch (error) { this.failed(error); return; }
    this.invalidatePreview(); const location = this.state.warehouse.location, ticket = this.begin(role), search = role === 'source' ? this.state.sourceSearch : this.state.destinationSearch;
    this.patch(role === 'source' ? { sourcePage: page(), sourceOffset: offset, source: null, loadingSource: true, error: null }
      : { destinationPage: page(), destinationOffset: offset, destination: null, loadingDestination: true, error: null });
    try { const result = await this.options.gateway.packages(this.options.actorId, location, role, search, offset, ticket.signal, ticket.current);
      if (ticket.current()) this.patch(role === 'source' ? { sourcePage: result } : { destinationPage: result }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch(role === 'source' ? { loadingSource: false } : { loadingDestination: false }); this.checkSession(); }
  }
  choosePackage(role: PackRole, id: string) {
    if (!this.mayEdit() || this.state.mode !== 'prepare' || (role === 'source' ? this.state.loadingSource : this.state.loadingDestination)) return;
    const selected = (role === 'source' ? this.state.sourcePage : this.state.destinationPage).items.find(item => item.skuId === id);
    if (id && !selected) return; this.invalidatePreview(); this.patch(role === 'source' ? { source: selected ?? null } : { destination: selected ?? null });
  }
  changeDraft(field: 'sourceRatio' | 'destinationRatio' | 'reason', value: string) {
    if (!this.mayEdit()) return; this.invalidatePreview(); this.patch({ [field]: value, error: null });
  }
  async previewRule() {
    if (!this.mayEdit() || this.state.mode !== 'prepare' || this.state.loadingSource || this.state.loadingDestination) return;
    try {
      const { warehouse, source, destination, sourceRatio, destinationRatio } = this.state;
      if (!warehouse || !source || !destination || !this.state.sourcePage.items.includes(source) || !this.state.destinationPage.items.includes(destination))
        throw new AccessFailure(400, 'pack_conversion_selection_required');
      const input = preparePackInput(warehouse.location, source, destination, sourceRatio, destinationRatio), ticket = this.begin('preview');
      this.patch({ preview: null, loadingPreview: true, error: null });
      try { const preview = parsePackPreview(await this.options.gateway.preview(this.options.actorId, input, ticket.signal, ticket.current), input);
        if (!ticket.current()) return;
        if (stableContent(preview.source) !== stableContent(source) || stableContent(preview.destination) !== stableContent(destination)) throw new AccessFailure(502, 'pack_conversion_response_invalid');
        this.patch({ preview }); }
      catch (error) { if (ticket.current()) this.failed(error); }
      finally { if (ticket.current()) this.patch({ loadingPreview: false }); this.checkSession(); }
    } catch (error) { this.failed(error); }
  }
  reviewProposal() {
    this.refreshReferences(); if (!this.mayEdit()) return;
    try { if (!this.state.preview || this.state.loadingPreview) throw new AccessFailure(400, 'pack_conversion_preview_required');
      this.patch({ review: preparePackProposal(this.options.actorId, this.state.preview, this.state.reason), error: null }); }
    catch (error) { this.failed(error); }
  }
  async loadProposals(beforeId: string | null = null) {
    if (!this.mayEdit() || !this.state.warehouse || this.state.mode !== 'proposals') return;
    this.cancel('detail'); this.cancel('decision'); const ticket = this.begin('proposals');
    this.patch({ proposals: cursor(), proposalCursor: beforeId, selected: null, decisionPreview: null, reason: '', loadingDetail: false, loadingDecision: false, loadingProposals: true, error: null });
    try { const proposals = await this.options.gateway.proposals(this.options.actorId, this.state.warehouse.location, beforeId, ticket.signal, ticket.current); if (ticket.current()) this.patch({ proposals }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingProposals: false }); this.checkSession(); }
  }
  async chooseProposal(id: string) {
    if (!this.mayEdit() || !this.state.warehouse || this.state.loadingProposals || !this.state.proposals.items.some(item => item.id === id)) return;
    this.cancel('decision'); const ticket = this.begin('detail'), location = this.state.warehouse.location;
    this.patch({ selected: null, decisionPreview: null, reason: '', loadingDetail: true, loadingDecision: false, error: null });
    try { const selected = parsePackProposal(await this.options.gateway.proposal(this.options.actorId, id, ticket.signal, ticket.current));
      if (!ticket.current()) return; if (selected.id !== id || stockLocationKey(selected.input.location) !== stockLocationKey(location)) throw new AccessFailure(502, 'pack_conversion_response_invalid');
      this.patch({ selected }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingDetail: false }); this.checkSession(); }
  }
  async previewDecision(action: PackAction) {
    if (!this.mayEdit() || !this.state.selected || this.state.loadingDetail) return;
    const selected = this.state.selected; if (selected.status !== 'pending' || !selected.canReview) { this.failed(new AccessFailure(403, 'independent_person_required')); return; }
    const ticket = this.begin('decision'); this.patch({ decisionPreview: null, loadingDecision: true, error: null });
    try { const preview = parsePackDecisionPreview(await this.options.gateway.review(this.options.actorId, selected, action, ticket.signal, ticket.current), selected, action);
      if (ticket.current()) this.patch({ decisionPreview: preview }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingDecision: false }); this.checkSession(); }
  }
  reviewDecision() {
    this.refreshReferences(); if (!this.mayEdit()) return;
    try { if (!this.state.decisionPreview || this.state.loadingDecision) throw new AccessFailure(400, 'pack_conversion_preview_required');
      this.patch({ review: preparePackDecision(this.options.actorId, this.state.decisionPreview, this.state.reason), error: null }); }
    catch (error) { this.failed(error); }
  }
  async loadRules(beforeId: string | null = null) {
    if (!this.mayEdit() || !this.state.warehouse || this.state.mode !== 'rules') return;
    const ticket = this.begin('rules'); this.patch({ rules: cursor(), ruleCursor: beforeId, loadingRules: true, error: null });
    try { const rules = await this.options.gateway.rules(this.options.actorId, this.state.warehouse.location, beforeId, ticket.signal, ticket.current); if (ticket.current()) this.patch({ rules }); }
    catch (error) { if (ticket.current()) this.failed(error); }
    finally { if (ticket.current()) this.patch({ loadingRules: false }); this.checkSession(); }
  }
  cancelReview() { if (this.guard() && !this.state.sending && !this.state.recovering) this.patch({ review: null, error: null }); }
  async submitReviewed(confirmed: boolean) {
    if (!confirmed || !this.state.review || !this.guard() || this.state.sending || this.state.recovering || this.state.attempt) return;
    this.refreshReferences(); if (this.state.references.length || this.state.recoveryError) return; await this.send(this.state.review);
  }
  private async send(command: PackCommand) {
    if (command.actorId !== this.options.actorId) return; const generation = this.generation, current = () => this.isCurrent() && generation === this.generation;
    this.cancelAll(); this.patch({ sending: true, review: null, attempt: command, result: null, error: null });
    try { const result = await submitTrackedRequest(this.ledger, this.journal, this.options.actorId, 'pack-rule', command.operation, command, async key => {
      this.activeReference = { operation: command.operation, key }; const saved = await this.options.gateway.submit(command, key, current);
      if (!current()) throw new AccessFailure(503, 'commit_outcome_unknown'); return packOutcome(saved, command);
    });
      if (current()) { this.activeReference = null; this.patch({ ...this.cleared(), attempt: null, result }); }
    } catch (error) { if (this.active) {
      const uncertain = this.ledger.isUncertain('pack-rule'); this.patch({ attempt: this.isCurrent() && uncertain ? command : null, error });
      if (!uncertain) { this.activeReference = null; this.patch({ preview: null, decisionPreview: null }); } this.failed(error);
    } } finally { if (this.active) { this.patch({ sending: false }); this.refreshReferences(); this.checkSession(); } }
  }
  setRecovering = (recovering: boolean) => {
    if (!this.active) return; this.patch({ recovering });
    if (!recovering && this.mayEdit() && !this.state.warehouses.length && !this.state.loadingWarehouses) void this.loadWarehouses();
  };
  receipt = async (entry: OperationReference) => {
    const generation = this.generation, current = () => this.isCurrent() && generation === this.generation && entry.actorId === this.options.actorId;
    packOperation(entry.operation); if (!current()) throw new AccessFailure(409, 'recovery_request_stale');
    const receipt = await this.options.gateway.receipt(entry, undefined, current); if (!current()) throw new AccessFailure(409, 'recovery_request_stale'); return receipt;
  };
  recoveryFailed = (error: unknown) => { this.failed(error); this.refreshReferences(); };
  reconciled = () => {
    this.refreshReferences(); if (this.state.recoveryError) return;
    if (!this.activeReference || !this.state.references.some(entry => entry.operation === this.activeReference!.operation && entry.key === this.activeReference!.key)) {
      this.ledger.resetAfterReconciliation(); this.activeReference = null; this.patch({ ...this.cleared(), attempt: null, error: null });
    }
  };
}
