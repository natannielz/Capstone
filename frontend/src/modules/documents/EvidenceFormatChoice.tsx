import {useId} from 'react';
import common from '@/modules/administration/access.module.css';

/** The choice selects a durable admission purpose before file inspection; a MIME label never chooses a route. */
export function EvidenceFormatChoice({value, disabled, onChange}: {value: 'image' | 'pdf' | null; disabled: boolean; onChange: (value: 'image' | 'pdf') => void}) {
  const id = useId();
  return <fieldset className={common.choiceGroup} disabled={disabled}><legend>Jenis bukti asli yang akan diunggah</legend>
    <label className={common.checkbox}><input type="radio" name={`evidence-format-${id}`} value="image" checked={value === 'image'} onChange={() => onChange('image')}/><span>Foto PNG atau JPEG</span></label>
    <label className={common.checkbox}><input type="radio" name={`evidence-format-${id}`} value="pdf" checked={value === 'pdf'} onChange={() => onChange('pdf')}/><span>Dokumen PDF asli</span></label>
    <p className={common.help}>Pilih jenis bukti terlebih dahulu. Kedua jenis paling besar 10 MiB dan harus lolos pemeriksaan keamanan. PDF sampai 10 halaman, tanpa enkripsi, formulir, lampiran, atau JavaScript.</p>
    {value === null && <p role="status">Belum ada jenis bukti yang dipilih.</p>}
  </fieldset>;
}
