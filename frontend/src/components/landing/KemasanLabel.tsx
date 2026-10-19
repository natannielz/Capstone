import { QuantityStepper } from "../workspace-kit/quantity-stepper";
import { CARTON } from "./kemasan-timeline";
import styles from "./landing.module.css";

export const DEMO_MINIMUM = 2, DEMO_MAXIMUM = 4;

export function kemasanSummary(cartons: number): string {
  return `${cartons} karton berisi ${cartons * CARTON.perCarton} botol`;
}

/** A shipping label: real fields, and the stepper the order screen uses. The total is announced politely. */
export function KemasanLabel({ value, cartons, onChange }: { value: string; cartons: number; onChange: (text: string) => void }) {
  const outOfRange = value.trim() !== String(cartons);
  return <aside className={styles.label} aria-labelledby="contoh-kemasan">
    <h2 id="contoh-kemasan" className={styles.labelTitle}>Contoh kemasan</h2>
    <dl className={styles.labelFields}>
      <div><dt>Barang</dt><dd>Air mineral 600 ml</dd></div>
      <div><dt>Isi per kemasan</dt><dd>{CARTON.perCarton} botol per karton</dd></div>
      <div><dt>Minimum</dt><dd>{DEMO_MINIMUM} karton</dd></div>
    </dl>
    <div className={styles.labelQuantity}>
      <QuantityStepper id="contoh-jumlah" label="Jumlah karton" value={value} minimum={DEMO_MINIMUM} increment={1} maximum={DEMO_MAXIMUM} onChange={onChange} />
      <p className={styles.total}>
        <span className={styles.totalNumber} aria-hidden="true">{cartons * CARTON.perCarton}</span>
        <span className={styles.totalText} aria-live="polite">{kemasanSummary(cartons)}</span>
      </p>
    </div>
    <p className={styles.labelNote}>{outOfRange ? `Contoh ini memakai ${DEMO_MINIMUM} sampai ${DEMO_MAXIMUM} karton.` : "Jumlah selalu bulat per kemasan. Harga dan ketersediaan mengikuti katalog akun Anda."}</p>
  </aside>;
}
