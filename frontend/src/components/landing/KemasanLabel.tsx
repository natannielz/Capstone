import { QuantityStepper } from "../workspace-kit/quantity-stepper";
import { COFFEE_MAXIMUM, COFFEE_MINIMUM } from "./journey";
import styles from "./landing.module.css";

export const SACHETS_PER_DUS = 24, OTHER_PACKAGES = 4;

export function kardusSummary(coffee: number): string {
  return `${coffee + OTHER_PACKAGES} kemasan dalam 1 kardus, termasuk ${coffee * SACHETS_PER_DUS} sachet kopi`;
}

const FIXED = [
  { item: "Kertas HVS A4 70 gsm", pack: "1 rim isi 500 lembar" },
  { item: "Teh celup melati", pack: "1 kotak isi 25 kantong" },
  { item: "Pulpen hitam", pack: "1 dus isi 12 batang" },
  { item: "Penyanitasi tangan 500 ml", pack: "1 botol" },
];

/** The request the box carries: real catalog packages, and the stepper the order screen uses. The total is announced politely. */
export function KemasanLabel({ value, coffee, onChange }: { value: string; coffee: number; onChange: (text: string) => void }) {
  const outOfRange = value.trim() !== String(coffee);
  return <aside className={styles.label} aria-labelledby="isi-kardus">
    <h2 id="isi-kardus" className={styles.labelTitle}>Isi kardus permintaan PB-000142</h2>
    <ul className={styles.labelItems}>
      <li className={styles.labelVariable}>
        <div><p className={styles.labelItem}>Kopi bubuk sachet</p><p className={styles.labelPack}>dus isi {SACHETS_PER_DUS} sachet</p></div>
        <QuantityStepper id="contoh-jumlah" label="Jumlah dus" value={value} minimum={COFFEE_MINIMUM} increment={1} maximum={COFFEE_MAXIMUM} onChange={onChange} />
      </li>
      {FIXED.map(row => <li key={row.item}><p className={styles.labelItem}>{row.item}</p><p className={styles.labelPack}>{row.pack}</p></li>)}
    </ul>
    <p className={styles.total}>
      <span className={styles.totalNumber} aria-hidden="true">{coffee + OTHER_PACKAGES}</span>
      <span className={styles.totalText} aria-live="polite">{kardusSummary(coffee)}</span>
    </p>
    <p className={styles.labelNote}>{outOfRange
      ? `Contoh ini memakai ${COFFEE_MINIMUM} sampai ${COFFEE_MAXIMUM} dus kopi.`
      : "Jumlah selalu bulat per kemasan. Harga dan ketersediaan mengikuti katalog akun Anda."}</p>
  </aside>;
}
