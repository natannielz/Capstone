'use client';

import Link from 'next/link';
import {useEffect, useState} from 'react';
import {createReplenishmentGateway, type ReplenishmentEvaluation} from '../inventory/replenishment-client';
import styles from '../administration/access.module.css';

export function ReplenishmentSuggestion({actorId, disabled, current, onUse}: {actorId: string; disabled: boolean; current: () => boolean;
  onUse: (value: ReplenishmentEvaluation) => Promise<void>}) {
  const [gateway] = useState(createReplenishmentGateway), [value, setValue] = useState<ReplenishmentEvaluation | null>(null), [failed, setFailed] = useState(false), [reference, setReference] = useState<string | null>(null);
  useEffect(() => {let active = true; const controller = new AbortController(); const id = new URL(window.location.href).searchParams.get('replenishment');
    void Promise.resolve().then(async () => {setReference(id); if (!id) return;
      try {const result = await gateway.read(id, {actorId, signal: controller.signal, isCurrent: () => active && current()});
        if (active && current()) {setValue(result); setFailed(result === null);}
      } catch {if (active) setFailed(true);}
    }); return () => {active = false; controller.abort();};
  }, [actorId, gateway, current]);
  if (!reference) return null;
  if (failed) return <section className={styles.review}><h2>Saran kebutuhan belum tersedia</h2><p>Akun dan penugasan harus dapat membaca pemeriksaan sumber stok tersebut. Pilih barang secara mandiri atau kembali untuk memeriksa sumbernya.</p><Link href="/inventory/replenishment">Periksa kebutuhan pembelian</Link></section>;
  if (!value) return <p role="status">Memeriksa saran kebutuhan tersimpan…</p>;
  const quantity = value.suggestedPurchasePackages;
  const usable = quantity !== null && BigInt(quantity) > BigInt(0) && BigInt(quantity) <= BigInt(1000000);
  return <section className={styles.review}><h2>Saran dari pemeriksaan kebutuhan</h2>
    <p>{value.skuCode} · {value.packageLabel} · {value.baseUnitsPerPackage} {value.baseUnitCode} per kemasan</p>
    <p>{usable ? `Saran tersimpan: ${new Intl.NumberFormat('id-ID').format(BigInt(quantity!))} kemasan.` : 'Belum ada jumlah yang dapat dimasukkan ke permintaan ini.'}</p>
    <p>Diamati {new Intl.DateTimeFormat('id-ID', {dateStyle: 'medium', timeStyle: 'short', timeZone: 'Asia/Jakarta'}).format(new Date(value.observedAt))} WIB. Saran ini merupakan catatan pada waktu tersebut. Tinjau perubahan kebutuhan sebelum mengajukan.</p>
    <p>Lokasi dan kemasan diperiksa kembali sebelum dimasukkan. Anda tetap memilih jalur, tanggal dan alasan, lalu meninjau permintaan.</p>
    <div className={styles.actions}><button type="button" className={styles.primary} disabled={disabled || !usable} onClick={() => void onUse(value)}>Gunakan saran dalam formulir</button>
      <Link href={'/inventory/replenishment?review=' + value.id}>Buka dasar perhitungan</Link></div>
  </section>;
}
