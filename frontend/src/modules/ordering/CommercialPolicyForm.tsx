'use client';

import { useId, useState, type FormEvent } from 'react';
import { RequestNotSentError } from '../../api/client';
import { commercialPolicyCharge, commercialPolicyTaxRate, commercialPolicyTime, type CommercialPolicyRule } from './order-commercial-policy-client';
import styles from '../administration/access.module.css';

export type CommercialPolicyFormValue = { rule: CommercialPolicyRule; validFrom: string; validUntil: string; reason: string };
const applicabilityLabels = { budget: 'Anggaran', costCenter: 'Pusat biaya', spending: 'Persetujuan belanja', creditHold: 'Penahanan kapasitas kredit' } as const;
const weekdays = ['Minggu', 'Senin', 'Selasa', 'Rabu', 'Kamis', 'Jumat', 'Sabtu'];

export function CommercialPolicyForm({ disabled, onPreview, onFailure, onDirty }: {
  disabled: boolean; onPreview: (value: CommercialPolicyFormValue) => void; onFailure: (error: unknown) => void; onDirty: () => void;
}) {
  const id = useId(), [taxTreatment, setTaxTreatment] = useState(''), [dayRule, setDayRule] = useState('');
  const [holiday, setHoliday] = useState(''), [holidays, setHolidays] = useState<string[]>([]);
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (disabled) return;
    try {
      const data = new FormData(event.currentTarget), text = (name: string) => String(data.get(name) ?? '').normalize('NFKC').trim();
      const applicability = Object.fromEntries(Object.keys(applicabilityLabels).map(name => [name, text(name)])) as CommercialPolicyRule['applicability'];
      const termDays = text('termDays'); if (!/^(?:0|[1-9][0-9]{0,2})$/.test(termDays) || Number(termDays) > 365) throw new RequestNotSentError(400, 'commercial_policy_invalid');
      onPreview({ rule: {
        applicability, charges: commercialPolicyCharge(text('charges')), taxTreatment: text('taxTreatment') as CommercialPolicyRule['taxTreatment'],
        taxRate: commercialPolicyTaxRate(text('taxRate')), exemptionReference: text('exemptionReference') || null,
        taxBase: text('taxBase') as CommercialPolicyRule['taxBase'], taxRounding: text('taxRounding') as CommercialPolicyRule['taxRounding'],
        rounding: text('rounding') as CommercialPolicyRule['rounding'], terms: {
          grouping: text('grouping') as CommercialPolicyRule['terms']['grouping'], termOrigin: text('termOrigin') as CommercialPolicyRule['terms']['termOrigin'],
          termDays: Number(termDays), dayRule: text('dayRule') as CommercialPolicyRule['terms']['dayRule'],
          workingDays: data.getAll('workingDays').map(value => Number(value)), holidays,
          numberingProcedure: text('numberingProcedure'), reissueProcedure: text('reissueProcedure'), taxDocumentProcess: text('taxDocumentProcess'),
        },
      }, validFrom: commercialPolicyTime(text('validFrom')), validUntil: commercialPolicyTime(text('validUntil')), reason: text('reason') });
    } catch (error) { onFailure(error); }
  }
  return <form onSubmit={submit} onChange={onDirty} className={styles.form}>
    <fieldset className={styles.fields} disabled={disabled}><legend>Penerapan kewajiban</legend>
      <p className={styles.help}>Pilih berdasarkan keputusan resmi. Penetapan ini tidak menyediakan saldo anggaran, kapasitas kredit, persetujuan pesanan, atau invoice.</p>
      {Object.entries(applicabilityLabels).map(([name, label]) => <label key={name} htmlFor={id + '-' + name}><span id={id + '-' + name + '-label'}>{label}</span><select id={id + '-' + name} aria-labelledby={id + '-' + name + '-label'} name={name} required defaultValue="">
        <option value="">Pilih penerapan</option><option value="required">Wajib dipenuhi</option><option value="not_applicable">Tidak berlaku berdasarkan keputusan resmi</option>
      </select></label>)}
    </fieldset>
    <fieldset className={styles.fields} disabled={disabled}><legend>Biaya dan pajak</legend>
      <label htmlFor={id + '-charges'}>Biaya tetap per pesanan (IDR)<input id={id + '-charges'} name="charges" required inputMode="decimal" maxLength={18} aria-describedby={id + '-money'} /></label>
      <small id={id + '-money'}>Tanpa pemisah ribuan, misalnya 15000.00. Isi 0 bila keputusan resmi menetapkan tanpa biaya.</small>
      <label htmlFor={id + '-taxTreatment'}><span id={id + '-taxTreatment-label'}>Perlakuan pajak</span><select id={id + '-taxTreatment'} aria-labelledby={id + '-taxTreatment-label'} name="taxTreatment" required value={taxTreatment} onChange={event => setTaxTreatment(event.target.value)}>
        <option value="">Pilih perlakuan</option><option value="taxable_exclusive">Pajak ditambahkan di luar harga</option><option value="exempt">Dibebaskan berdasarkan keputusan resmi</option>
      </select></label>
      <label htmlFor={id + '-taxRate'}>Tarif pajak (%)<input id={id + '-taxRate'} name="taxRate" required inputMode="decimal" maxLength={8} aria-describedby={id + '-tax'} /></label>
      <small id={id + '-tax'}>Tarif yang disetujui, 0–100 dengan paling banyak empat angka desimal. Pembebasan harus memakai tarif 0 dan rujukan keputusan.</small>
      {taxTreatment === 'exempt' && <label htmlFor={id + '-exemptionReference'}>Rujukan pembebasan<textarea id={id + '-exemptionReference'} name="exemptionReference" required maxLength={1000} rows={2} /></label>}
      <label htmlFor={id + '-taxBase'}><span id={id + '-taxBase-label'}>Dasar pengenaan</span><select id={id + '-taxBase'} aria-labelledby={id + '-taxBase-label'} name="taxBase" required defaultValue=""><option value="">Pilih dasar</option><option value="subtotal">Subtotal barang</option><option value="subtotal_and_charges">Subtotal barang dan biaya tetap</option></select></label>
      <label htmlFor={id + '-taxRounding'}><span id={id + '-taxRounding-label'}>Tingkat pembulatan</span><select id={id + '-taxRounding'} aria-labelledby={id + '-taxRounding-label'} name="taxRounding" required defaultValue=""><option value="">Pilih tingkat</option><option value="line">Per baris barang</option><option value="document">Total dokumen</option></select></label>
      <label htmlFor={id + '-rounding'}><span id={id + '-rounding-label'}>Aturan pembulatan</span><select id={id + '-rounding'} aria-labelledby={id + '-rounding-label'} name="rounding" required defaultValue=""><option value="">Pilih aturan</option><option value="half_even">Nilai tengah ke angka genap</option><option value="half_up">Nilai tengah ke atas</option></select></label>
    </fieldset>
    <fieldset className={styles.fields} disabled={disabled}><legend>Syarat tagihan</legend>
      <label htmlFor={id + '-grouping'}><span id={id + '-grouping-label'}>Pengelompokan</span><select id={id + '-grouping'} aria-labelledby={id + '-grouping-label'} name="grouping" required defaultValue=""><option value="">Pilih pengelompokan</option><option value="receipt">Per bukti penerimaan</option><option value="buying_branch_period_channel">Per cabang, periode, dan kanal toko</option><option value="buying_org_period_channel">Per organisasi, periode, dan kanal toko</option></select></label>
      <label htmlFor={id + '-termOrigin'}><span id={id + '-termOrigin-label'}>Awal perhitungan jatuh tempo</span><select id={id + '-termOrigin'} aria-labelledby={id + '-termOrigin-label'} name="termOrigin" required defaultValue=""><option value="">Pilih awal</option><option value="issue_date">Tanggal penerbitan</option><option value="earliest_receipt">Penerimaan pertama</option><option value="latest_receipt">Penerimaan terakhir</option></select></label>
      <label htmlFor={id + '-termDays'}>Jangka pembayaran (hari)<input id={id + '-termDays'} name="termDays" required type="number" min={0} max={365} step={1} /></label>
      <label htmlFor={id + '-dayRule'}><span id={id + '-dayRule-label'}>Perhitungan hari</span><select id={id + '-dayRule'} aria-labelledby={id + '-dayRule-label'} name="dayRule" required value={dayRule} onChange={event => setDayRule(event.target.value)}><option value="">Pilih perhitungan</option><option value="calendar_days">Hari kalender</option><option value="working_days">Hari kerja yang ditetapkan</option></select></label>
      {dayRule === 'working_days' && <fieldset className={styles.fields}><legend>Hari kerja</legend>{weekdays.map((day, index) => <label className={styles.checkbox} htmlFor={id + '-workingDay-' + index} key={day}><input id={id + '-workingDay-' + index} name="workingDays" value={index} type="checkbox" />{day}</label>)}</fieldset>}
      <label htmlFor={id + '-holiday'}>Tanggal libur yang ditetapkan<input id={id + '-holiday'} type="date" value={holiday} onChange={event => setHoliday(event.target.value)} /></label>
      <button type="button" className={styles.secondary} disabled={!holiday || holidays.includes(holiday) || holidays.length >= 730} onClick={() => { setHolidays(values => [...values, holiday].sort()); setHoliday(''); onDirty(); }}>Tambahkan tanggal libur</button>
      {holidays.length > 0 && <ul>{holidays.map(date => <li key={date}>{date} <button type="button" className={styles.secondary} onClick={() => { setHolidays(values => values.filter(value => value !== date)); onDirty(); }}>Hapus tanggal {date}</button></li>)}</ul>}
      <label htmlFor={id + '-numberingProcedure'}>Rujukan prosedur penomoran<textarea id={id + '-numberingProcedure'} name="numberingProcedure" required maxLength={1000} rows={2} /></label>
      <label htmlFor={id + '-reissueProcedure'}>Rujukan koreksi dan penerbitan ulang<textarea id={id + '-reissueProcedure'} name="reissueProcedure" required maxLength={1000} rows={2} /></label>
      <label htmlFor={id + '-taxDocumentProcess'}>Rujukan proses dokumen pajak<textarea id={id + '-taxDocumentProcess'} name="taxDocumentProcess" required maxLength={1000} rows={2} /></label>
    </fieldset>
    <fieldset className={styles.fields} disabled={disabled}><legend>Masa berlaku dan alasan</legend>
      <div className={styles.twoFields}><label htmlFor={id + '-validFrom'}>Mulai berlaku (WIB)<input id={id + '-validFrom'} name="validFrom" required type="datetime-local" /></label><label htmlFor={id + '-validUntil'}>Berakhir (WIB)<input id={id + '-validUntil'} name="validUntil" required type="datetime-local" /></label></div>
      <label htmlFor={id + '-reason'}>Alasan pengajuan<textarea id={id + '-reason'} name="reason" required maxLength={1000} rows={3} /></label>
      <button className={styles.secondary}>Periksa rincian pengajuan</button>
    </fieldset>
  </form>;
}
