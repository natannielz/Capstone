'use client';

import Link from 'next/link';
import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { createIdentityGateway } from '../identity/identity-client';
import { IdentityFailure, sessionTiming, canUseOrganizationAddresses, type IdentitySession } from '../identity/identity-workflow';
import { createAccessGateway } from '../administration/access-client';
import { AccessFailure, MutationLedger, accessMessage } from '../administration/workflow';
import { AdministrationNavigation } from '../administration/AdministrationNavigation';
import { OperationRecovery, useOperationRecovery } from '../administration/OperationRecovery';
import { submitTrackedRequest } from '../administration/operation-journal';
import { organizationKinds } from './organization-workflow';
import { addressPurposeLabels, createOrganizationAddressGateway, organizationAddressInput, prepareOrganizationAddress, prepareOrganizationAddressDecision,
  type PostalAddress, type OrganizationAddressPurpose, type OrganizationAddressChoice, type OrganizationAddressCurrent,
  type OrganizationAddressProposal, type OrganizationAddressPreview, type OrganizationAddressDecisionPreview, type OrganizationAddressDecision } from './organization-address-client';
import styles from '../administration/access.module.css';
import orgStyles from './organizations.module.css';
import { dateTimeFormatter } from '@/lib/format';
import { SessionAccessState } from "@/components/workspace-kit/session-access-state";

type Account = { actorId: string; actions: readonly string[]; session: IdentitySession; observedAt: number };
const path = '/administration/organizations/addresses';
const blank = (): PostalAddress => ({ line1: '', line2: null, city: '', region: '', postalCode: '', countryCode: '' });
const when = (value: string) => dateTimeFormatter.format(new Date(value));
const status = { pending: 'Menunggu pemeriksaan', approved: 'Disetujui', rejected: 'Ditolak' } as const;
function message(error: unknown) {
  if (error instanceof IdentityFailure) return 'Sesi belum tersedia. Masuk kembali untuk memeriksa alamat organisasi.';
  if (error instanceof AccessFailure) {
    if (error.code === 'organization_address_response_invalid' || error.code === 'organization_address_integrity_unavailable') return 'Riwayat alamat belum dapat dipastikan lengkap. Perubahan ditahan; hubungi penanggung jawab.';
    if (error.code === 'organization_address_changed' || error.code === 'organization_address_view_stale') return 'Data organisasi atau alamat berubah. Muat data terbaru dan periksa ulang rincian.';
    if (error.code === 'organization_address_invalid') return 'Isi alamat lengkap, pilih unit yang sesuai dan sertakan alasan.';
    if (error.code === 'independent_person_required') return 'Pengajuan ini harus diputuskan oleh orang lain yang memiliki penugasan pemeriksa.';
  }
  return accessMessage(error);
}
export function OrganizationAddressWorkspace() {
  const [identity] = useState(createIdentityGateway), [access] = useState(createAccessGateway);
  const [account, setAccount] = useState<Account | null>(null), [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(true), [revision, setRevision] = useState(0), [elapsed, setElapsed] = useState(0);
  useEffect(() => { let active = true; const controller = new AbortController(); void Promise.resolve().then(async () => {
    if (!active) return; setAccount(null); setLoading(true); setError(null);
    try {
      const session = await identity.session(controller.signal), observedAt = performance.now();
      if (!session.authenticated || !session.account) throw new IdentityFailure(401, 'unauthenticated');
      const current = await access.current(); if (!active) return;
      if (current.userId !== session.account.userId) throw new AccessFailure(409, 'actor_changed');
      if (!canUseOrganizationAddresses(current.allowedActions)) throw new AccessFailure(403, 'scope_denied');
      if (sessionTiming(session, performance.now() - observedAt).expired) throw new IdentityFailure(401, 'session_expired');
      setAccount({ actorId: current.userId, actions: current.allowedActions, session, observedAt }); setElapsed(performance.now() - observedAt);
    } catch (failure) { if (active) setError(failure); } finally { if (active) setLoading(false); }
  }); return () => { active = false; controller.abort(); }; }, [identity, access, revision]);
  useEffect(() => { const timer = window.setInterval(() => { if (account) setElapsed(performance.now() - account.observedAt); }, 1000);
    const restored = (event: PageTransitionEvent) => { if (event.persisted) { setAccount(null); setRevision(value => value + 1); } };
    window.addEventListener('pageshow', restored); return () => { window.clearInterval(timer); window.removeEventListener('pageshow', restored); }; }, [account]);
  const denied = useCallback((failure: unknown) => { if (failure instanceof AccessFailure && (failure.status === 401 || failure.code === 'actor_changed' || failure.code === 'session_expired')) { setAccount(null); setError(failure); } }, []);
  return <main className={styles.page}><header className={styles.header}><div><Link href="/identity/account" className={styles.brand}>Unit Toko</Link>
    <h1>Alamat organisasi</h1><p>Ajukan dan periksa alamat penagihan legal, pengiriman cabang dan keberangkatan gudang sesuai penugasan.</p></div></header>
    {error !== null && account !== null && <p role="alert" className={styles.alert}>{message(error)}</p>}
    {loading ? <p role="status">Memeriksa akun dan akses…</p> : !account || sessionTiming(account.session, elapsed).expired ? <SessionAccessState error={error} returnPath={path} onRetry={() => setRevision(value => value + 1)} />
      : <AddressDesk key={account.actorId + ':' + account.observedAt} account={account} fresh={sessionTiming(account.session, elapsed).fresh} onDenied={denied} />}
  </main>;
}
function PostalDetails({ value }: { value: PostalAddress }) {
  return <dl className={styles.facts}><div><dt>Alamat utama</dt><dd>{value.line1}</dd></div>{value.line2 && <div><dt>Alamat tambahan</dt><dd>{value.line2}</dd></div>}
    <div><dt>Kota / kabupaten</dt><dd>{value.city}</dd></div><div><dt>Provinsi / wilayah</dt><dd>{value.region}</dd></div>
    <div><dt>Kode pos</dt><dd>{value.postalCode}</dd></div><div><dt>Kode negara</dt><dd>{value.countryCode}</dd></div></dl>;
}
function PostalFields({ value, onChange }: { value: PostalAddress; onChange: (value: PostalAddress) => void }) {
  return <><label>Alamat utama<input required maxLength={200} autoComplete="off" value={value.line1} onChange={event => onChange({ ...value, line1: event.target.value })} /></label>
    <label>Alamat tambahan <span className={styles.optional}>(opsional)</span><input maxLength={200} autoComplete="off" value={value.line2 ?? ''} onChange={event => onChange({ ...value, line2: event.target.value || null })} /></label>
    <div className={styles.twoFields}><label>Kota / kabupaten<input required maxLength={80} autoComplete="off" value={value.city} onChange={event => onChange({ ...value, city: event.target.value })} /></label>
      <label>Provinsi / wilayah<input required maxLength={80} autoComplete="off" value={value.region} onChange={event => onChange({ ...value, region: event.target.value })} /></label></div>
    <div className={styles.twoFields}><label>Kode pos<input required maxLength={20} autoComplete="off" value={value.postalCode} onChange={event => onChange({ ...value, postalCode: event.target.value })} /></label>
      <label><span id="address-country-label">Kode negara</span><input required minLength={2} maxLength={2} autoComplete="off" value={value.countryCode} aria-labelledby="address-country-label" aria-describedby="address-country-help" onChange={event => onChange({ ...value, countryCode: event.target.value })} />
        <small id="address-country-help">Masukkan dua huruf sesuai negara alamat.</small></label></div></>;
}
function AddressDesk({ account, fresh, onDenied }: { account: Account; fresh: boolean; onDenied: (failure: unknown) => void }) {
  const [gateway] = useState(createOrganizationAddressGateway), recovery = useOperationRecovery(account.actorId);
  const keyPrefix = useRef('oa'), [ledger] = useState(() => new MutationLedger(() => `${keyPrefix.current}.${crypto.randomUUID()}`));
  const [purpose, setPurpose] = useState<OrganizationAddressPurpose>('legal_billing'), [targets, setTargets] = useState<OrganizationAddressChoice[]>([]);
  const [selected, setSelected] = useState<OrganizationAddressChoice | null>(null), [address, setAddress] = useState<OrganizationAddressCurrent | null>(null);
  const [page, setPage] = useState<{ items: OrganizationAddressProposal[]; nextProposalId: string | null }>({ items: [], nextProposalId: null });
  const [proposal, setProposal] = useState<OrganizationAddressProposal | null>(null), [editing, setEditing] = useState(false), [postal, setPostal] = useState(blank);
  const [reason, setReason] = useState(''), [proposalReview, setProposalReview] = useState<{ preview: OrganizationAddressPreview; command: ReturnType<typeof prepareOrganizationAddress> } | null>(null);
  const [decisionReason, setDecisionReason] = useState(''), [decisionReview, setDecisionReview] = useState<{ preview: OrganizationAddressDecisionPreview; command: ReturnType<typeof prepareOrganizationAddressDecision> } | null>(null);
  const [confirmed, setConfirmed] = useState(false), [busy, setBusy] = useState(false), [recovering, setRecovering] = useState(false), [offline, setOffline] = useState(false);
  const [error, setError] = useState<unknown>(null), [notice, setNotice] = useState(''), [revision, setRevision] = useState(0);
  const alive = useRef(false), pending = useRef(false), generation = useRef(0), reviewHeading = useRef<HTMLHeadingElement>(null);
  const current = useCallback(() => alive.current && navigator.onLine && !sessionTiming(account.session, performance.now() - account.observedAt).expired, [account]);
  const locked = busy || recovering || offline, commandLocked = locked || recovery.locked;
  const canPropose = account.actions.includes('technical.configure'), canDecide = account.actions.includes('organization.approve');
  const canReadNow = account.actions.includes('organization.address.read') || canPropose || canDecide && fresh;
  useEffect(() => { const lifetime = generation; alive.current = true; const connection = () => setOffline(!navigator.onLine); connection(); window.addEventListener('online', connection); window.addEventListener('offline', connection);
    return () => { alive.current = false; lifetime.current++; window.removeEventListener('online', connection); window.removeEventListener('offline', connection); }; }, []);
  useEffect(() => { let active = true; const ticket = ++generation.current; void Promise.resolve().then(async () => {
    if (!active || !current()) return; setBusy(true); setTargets([]); setSelected(null); setAddress(null); setPage({ items: [], nextProposalId: null }); clearReview(); setError(null);
    if (!canReadNow) { setBusy(false); return; }
    try { const result = await gateway.targets(account.actorId, purpose); if (active && current() && ticket === generation.current) setTargets(result.items); }
    catch (failure) { if (active && current() && ticket === generation.current) { setError(failure); onDenied(failure); } }
    finally { if (active && ticket === generation.current) setBusy(false); }
  }); return () => { active = false; }; }, [gateway, account.actorId, purpose, revision, current, onDenied, canReadNow]);
  useEffect(() => { if (proposalReview || decisionReview) reviewHeading.current?.focus(); }, [proposalReview, decisionReview]);
  useEffect(() => { if (!busy && recovery.entries.length === 0) return; const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = ''; };
    window.addEventListener('beforeunload', warn); return () => window.removeEventListener('beforeunload', warn); }, [busy, recovery.entries.length]);
  function clearReview() { setProposal(null); setEditing(false); setProposalReview(null); setDecisionReview(null); setReason(''); setDecisionReason(''); setConfirmed(false); setPostal(blank()); }
  async function work(action: () => Promise<void>) {
    if (!current() || locked || pending.current) return; pending.current = true; setBusy(true); setError(null); setNotice('');
    try { await action(); } catch (failure) { if (alive.current) { setError(failure); onDenied(failure); } }
    finally { pending.current = false; recovery.refresh(); if (alive.current) setBusy(false); }
  }
  async function select(choice: OrganizationAddressChoice) {
    clearReview(); setSelected(null); setAddress(null); setPage({ items: [], nextProposalId: null });
    const [nextAddress, nextPage] = await Promise.all([gateway.read(account.actorId, choice.target, purpose), gateway.proposals(account.actorId, choice.target, purpose)]);
    if (current()) { setSelected(choice); setAddress(nextAddress); setPage(nextPage); }
  }
  async function open(source: OrganizationAddressProposal) {
    clearReview(); const result = await gateway.proposal(account.actorId, source.proposalId); if (current()) setProposal(result);
  }
  async function previewProposal(event: FormEvent) {
    event.preventDefault(); if (!selected || !canPropose || commandLocked) return;
    await work(async () => {
      const input = organizationAddressInput({ target: selected.target, purpose, expectedScopeVersion: selected.scopeVersion,
        expectedProfileVersion: selected.profileVersion, expectedAddressVersion: selected.addressVersion, address: postal });
      const preview = await gateway.preview(account.actorId, input), command = prepareOrganizationAddress(account.actorId, selected, input, preview, reason.normalize('NFKC').trim());
      if (current()) { setProposalReview({ preview, command }); setConfirmed(false); }
    });
  }
  async function submitProposal() {
    if (!proposalReview || !confirmed || commandLocked || !canPropose) return;
    const command = proposalReview.command; keyPrefix.current = `oa.${command.targetId}.${command.purpose}`;
    await work(async () => {
      const result = await submitTrackedRequest(ledger, recovery.journal, account.actorId, 'address-propose', 'organization.address.propose', command.body,
        key => gateway.propose(command, key, current));
      if (current()) { clearReview(); setProposal(await gateway.proposal(account.actorId, result.proposalId)); setNotice('Pengajuan tersimpan. Alamat aktif tetap menunggu keputusan pemeriksa lain.'); }
    });
  }
  async function previewDecision(value: OrganizationAddressDecision) {
    if (!proposal || !canDecide || !fresh || commandLocked || proposal.state !== 'pending') return;
    await work(async () => {
      const preview = await gateway.previewDecision(account.actorId, proposal, value), command = prepareOrganizationAddressDecision(account.actorId, proposal, preview, decisionReason.normalize('NFKC').trim());
      if (current()) { setDecisionReview({ preview, command }); setConfirmed(false); }
    });
  }
  async function saveDecision() {
    if (!decisionReview || !confirmed || !canDecide || !fresh || commandLocked) return;
    const command = decisionReview.command; keyPrefix.current = `oad.${command.proposalId}.${command.decision}`;
    await work(async () => {
      const result = await submitTrackedRequest(ledger, recovery.journal, account.actorId, 'address-decision', `organization.address.${command.decision}`, command.body,
        key => gateway.decide(command, key, () => current() && sessionTiming(account.session, performance.now() - account.observedAt).fresh));
      if (current()) { clearReview(); setProposal(await gateway.proposal(account.actorId, result.proposalId)); setNotice(command.decision === 'approve' ? 'Alamat telah disetujui. Muat data terbaru untuk melihat alamat aktif.' : 'Penolakan tersimpan; alamat aktif tidak berubah.'); }
    });
  }
  function reconcile() { ledger.resetAfterReconciliation(); clearReview(); recovery.refresh(); setRevision(value => value + 1); }
  return <>
    <AdministrationNavigation current="organization-addresses" actions={account.actions} locked={locked || recovery.locked} />
    {canDecide && !fresh && <p className={styles.alert}>Verifikasi kedua perlu diperbarui sebelum memeriksa atau memutuskan pengajuan alamat.
      {account.session.stepUpOperations.includes('organization.approve') && <Link href={`/identity/account?operation=organization.approve&returnPath=${encodeURIComponent(path)}`}>Perbarui verifikasi</Link>}</p>}
    {offline && <p role="status" className={styles.alert}>Koneksi terputus. Rincian dapat dibaca; pengajuan dan keputusan ditahan.</p>}
    {error !== null && <p role="alert" className={styles.alert}>{message(error)}</p>}{notice && <p role="status" className={styles.success}>{notice}</p>}
    {recovery.error !== null && <p role="alert" className={styles.alert}>{message(recovery.error)}</p>}
    <OperationRecovery entries={recovery.entries} journal={recovery.journal} disabled={locked} onResolved={reconcile} onFailure={onDenied} onBusyChange={setRecovering}
      recoveryAdapters={{ 'organization.address': { lookup: entry => gateway.recover(entry.actorId, entry.operation, entry.key) } }} />
    <section className={styles.accountSection} aria-label="Tujuan alamat"><div className={styles.twoFields}><label><span id="address-purpose-label">Tujuan penggunaan</span><select aria-labelledby="address-purpose-label" value={purpose} disabled={locked || recovery.locked}
      onChange={event => setPurpose(event.target.value as OrganizationAddressPurpose)}>{Object.entries(addressPurposeLabels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
      <label><span id="address-target-label">Organisasi / unit</span><select aria-labelledby="address-target-label" value={selected ? selected.target.kind + ':' + selected.target.id : ''} disabled={locked} onChange={event => {
        const choice = targets.find(item => item.target.kind + ':' + item.target.id === event.target.value); if (choice) void work(() => select(choice));
      }}><option value="">Pilih unit yang ditugaskan</option>{targets.map(item => <option key={item.target.kind + ':' + item.target.id} value={item.target.kind + ':' + item.target.id}>{item.code} · {item.name} — {organizationKinds[item.target.kind]}</option>)}</select></label></div>
      <p className={styles.help}>Alamat penagihan melekat pada organisasi legal; alamat pengiriman pada cabang / divisi; alamat keberangkatan pada gudang. Data ini tidak menetapkan penerima atau kontak verifikasi.</p>
      <button className={styles.secondary} disabled={locked} onClick={() => { clearReview(); setRevision(value => value + 1); }}>Muat data terbaru</button>
      {!busy && canReadNow && targets.length === 0 && <p>Belum ada unit aktif dengan nama dan kode yang disetujui dalam penugasan Anda untuk tujuan ini.</p>}
    </section>
    {busy && <p role="status">Memeriksa data alamat…</p>}
    {selected && address && <div className={styles.desk}>
      <section className={styles.queue} aria-labelledby="address-register-heading"><div className={styles.queueHeader}><h2 id="address-register-heading">Pengajuan alamat</h2><span>{selected.code} · {selected.name}</span></div>
        <ul className={styles.queueList}>{page.items.map(item => <li key={item.proposalId}><button className={proposal?.proposalId === item.proposalId ? styles.selectedRow : styles.queueRow} disabled={locked} onClick={() => void work(() => open(item))}>
          <strong>{item.address.line1}</strong><span>{status[item.state]} · {addressPurposeLabels[item.purpose]}</span><small>{when(item.proposedAt)} · Pengajuan {item.proposalId}</small></button></li>)}</ul>
        {page.items.length === 0 && <p className={styles.empty}>Belum ada pengajuan alamat.</p>}
        <nav className={styles.pagination} aria-label="Halaman pengajuan alamat"><button className={styles.secondary} disabled={locked} onClick={() => void work(() => select(selected))}>Terbaru</button>
          <button className={styles.secondary} disabled={locked || page.nextProposalId === null} onClick={() => void work(async () => { const result = await gateway.proposals(account.actorId, selected.target, purpose, page.nextProposalId); if (current()) { clearReview(); setPage(result); } })}>Berikutnya</button></nav>
      </section>
      <section className={styles.detail} aria-label="Rincian alamat organisasi">
        {!editing && !proposal && <div className={styles.form}><h2>{selected.code} · {selected.name}</h2><p>{addressPurposeLabels[purpose]} · {organizationKinds[selected.target.kind]}</p>
          {address.address ? <><h3>{address.current ? 'Alamat aktif yang disetujui' : 'Alamat terakhir disetujui'}</h3><PostalDetails value={address.address} /><p className={styles.help}>Disetujui {when(address.approvedAt!)} · Versi {address.version}</p>
            {!address.current && <p className={styles.alert}>Sumber organisasi berubah setelah alamat ini disetujui. Alamat tersimpan sebagai riwayat dan perlu diperbarui sebelum dipakai untuk pesanan baru.</p>}</> : <p>Belum ada alamat aktif yang disetujui untuk tujuan ini.</p>}
          {canPropose && <button className={styles.primary} disabled={commandLocked} onClick={() => { clearReview(); setEditing(true); }}>Ajukan alamat</button>}</div>}
        {editing && <form className={styles.form} onSubmit={event => void previewProposal(event)}><h2>Ajukan {addressPurposeLabels[purpose].toLowerCase()}</h2><p>{selected.code} · {selected.name}</p>
          <p className={styles.help}>Masukkan alamat dari sumber organisasi yang sah. Pengajuan tidak langsung mengubah alamat aktif.</p>
          <fieldset className={styles.fields} disabled={commandLocked || proposalReview !== null}><PostalFields value={postal} onChange={value => { setPostal(value); setProposalReview(null); }} />
            <label>Alasan pengajuan<textarea required rows={3} maxLength={1000} value={reason} onChange={event => { setReason(event.target.value); setProposalReview(null); }} /></label>
            <button className={styles.secondary}>Periksa rincian pengajuan</button></fieldset>
          {proposalReview && <section className={styles.review} aria-label="Rincian pengajuan alamat"><h3 tabIndex={-1} ref={reviewHeading}>Rincian yang akan diajukan</h3>
            <p>{proposalReview.preview.view.code} · {proposalReview.preview.view.name}<br />{addressPurposeLabels[proposalReview.preview.view.purpose]}</p><PostalDetails value={proposalReview.preview.view.address} />
            <p className={styles.preserve}>{proposalReview.command.body.reason}</p><p className={styles.help}>Versi unit {proposalReview.preview.view.scopeVersion} · Profil {proposalReview.preview.view.profileVersion} · Alamat {proposalReview.preview.view.addressVersion}</p>
            <label className={styles.checkbox}><input type="checkbox" checked={confirmed} disabled={commandLocked} onChange={event => setConfirmed(event.target.checked)} />Saya telah memeriksa unit, tujuan penggunaan dan seluruh rincian alamat.</label>
            <div className={styles.actions}><button type="button" className={styles.primary} disabled={commandLocked || !confirmed} onClick={() => void submitProposal()}>Kirim pengajuan alamat</button>
              <button type="button" className={styles.secondary} disabled={commandLocked} onClick={() => { setProposalReview(null); setConfirmed(false); }}>Ubah rincian</button></div></section>}
        </form>}
        {proposal && <div className={styles.form}><h2>{status[proposal.state]}</h2><p>{proposal.code} · {proposal.name}<br />{addressPurposeLabels[proposal.purpose]}</p><PostalDetails value={proposal.address} />
          <p className={styles.help}>Pengajuan {proposal.proposalId} · {when(proposal.proposedAt)} · Versi alamat dasar {proposal.baseVersion}</p>
          {proposal.state === 'approved' && <p role="status">Keputusan menghasilkan alamat versi {proposal.addressVersion}. Pengajuan asli tetap tersimpan.</p>}
          {proposal.state === 'pending' && !proposal.bindingCurrent && <p className={styles.alert}>Sumber organisasi atau alamat dasar telah berubah. Pengajuan ini tidak dapat disetujui; pemeriksa tetap dapat mencatat penolakan.</p>}
          {proposal.state === 'pending' && canDecide && <><p className={styles.help}>Keputusan memerlukan pemeriksa yang berbeda dari pengaju dan verifikasi kedua yang masih baru.</p>
            {!fresh && <p className={styles.alert}>Verifikasi kedua perlu diperbarui sebelum keputusan.{account.session.stepUpOperations.includes('organization.approve') && <Link href={`/identity/account?operation=organization.approve&returnPath=${encodeURIComponent(path)}`}>Perbarui verifikasi</Link>}</p>}
            <label>Alasan keputusan<textarea rows={3} maxLength={1000} required disabled={commandLocked || decisionReview !== null} value={decisionReason} onChange={event => { setDecisionReason(event.target.value); setDecisionReview(null); }} /></label>
            {!decisionReview && <div className={styles.actions}><button className={styles.primary} disabled={commandLocked || !fresh || !decisionReason.trim() || !proposal.bindingCurrent} onClick={() => void previewDecision('approve')}>Tinjau persetujuan alamat</button>
              <button className={styles.secondary} disabled={commandLocked || !fresh || !decisionReason.trim()} onClick={() => void previewDecision('reject')}>Tinjau penolakan alamat</button></div>}
          </>}
          {decisionReview && <section className={styles.review} aria-label="Rincian keputusan alamat"><h3 tabIndex={-1} ref={reviewHeading}>{decisionReview.command.decision === 'approve' ? 'Pastikan persetujuan alamat' : 'Pastikan penolakan alamat'}</h3>
            <table className={orgStyles.compare}><thead><tr><th>Alamat aktif</th><th>Pengajuan yang diperiksa</th></tr></thead><tbody><tr><td>{address.address ? <PostalDetails value={address.address} /> : 'Belum ditetapkan'}</td><td><PostalDetails value={decisionReview.preview.view.address} /></td></tr></tbody></table>
            <p className={styles.preserve}>{decisionReview.command.body.reason}</p><label className={styles.checkbox}><input type="checkbox" checked={confirmed} disabled={commandLocked || !fresh} onChange={event => setConfirmed(event.target.checked)} />Saya telah memeriksa alamat dan siap mencatat keputusan ini.</label>
            <div className={styles.actions}><button className={styles.primary} disabled={commandLocked || !fresh || !confirmed} onClick={() => void saveDecision()}>{decisionReview.command.decision === 'approve' ? 'Setujui alamat' : 'Simpan penolakan alamat'}</button>
              <button className={styles.secondary} disabled={commandLocked} onClick={() => { setDecisionReview(null); setConfirmed(false); }}>Kembali ke pengajuan</button></div></section>}
        </div>}
      </section>
    </div>}
  </>;
}
