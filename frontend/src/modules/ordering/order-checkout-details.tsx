import type { ReactNode } from 'react';
import { PackageOpen, ReceiptText, Truck } from 'lucide-react';
import type { CheckoutReview, SubmittedOrder } from './order-submit-client';
import { orderAmount } from './order-quote-client';
import styles from './order-checkout.module.css';

// Only a server-validated quote or immutable submitted order reaches this presentation.
export function OrderCheckoutDetails({ value, reviewing, children }: { value: CheckoutReview | SubmittedOrder; reviewing: boolean; children?: ReactNode }) {
  const delivery = value.input.delivery!;
  return <>
    <div className={styles.reviewHeading}><h2>{reviewing ? 'Periksa sebelum mengajukan' : 'Rincian pesanan tersimpan'}</h2><p>{value.quote.lines.length} jenis kemasan · {reviewing ? 'Periksa isi dan tujuan pengiriman' : 'Rincian saat pengajuan'}</p></div>
    <div className={styles.reviewGrid}><div className={styles.sections}>
      <section className={styles.panel} aria-label="Kemasan pesanan"><h3><PackageOpen size={20} aria-hidden="true" />Kemasan pesanan</h3>
        <ul className={styles.lines}>{value.quote.lines.map(line => <li key={line.definition.skuId}>
          <div className={styles.noPhoto}><PackageOpen size={26} strokeWidth={1.25} aria-hidden="true" /><span>Foto belum tersedia</span></div>
          <div className={styles.lineCopy}><h4>{line.familyName}</h4><p className={styles.sku}>{line.skuCode}</p>
            <p className={styles.packages}>{line.packages} {line.definition.packageLabel} · {line.baseUnits} {line.definition.baseUnitCode}</p>
            <p className={styles.lineNote}>Isi per {line.definition.packageLabel}: {line.definition.baseUnitsPerPackage} {line.definition.baseUnitCode}</p>
            <p className={styles.lineNote}>{orderAmount(value.quote.currency, line.price.unitPrice)} / {line.definition.packageLabel}</p>
          </div><strong className={styles.lineTotal}>{orderAmount(value.quote.currency, line.catalogSubtotal)}</strong>
        </li>)}</ul>
      </section>
      <section className={styles.panel} aria-label="Tujuan pengiriman"><h3><Truck size={20} aria-hidden="true" />Tujuan pengiriman</h3>
        <div className={styles.delivery}><p><strong>{delivery.recipientName}</strong><span>{delivery.phone}</span></p><p className={styles.address}>{delivery.address}{'\n'}{delivery.city}{delivery.postalCode ? ' ' + delivery.postalCode : ''}</p></div>
        {delivery.instructions && <div className={styles.instructions}><h4>Catatan pengiriman</h4><p>{delivery.instructions}</p></div>}
        {value.input.purchaseReference && <p className={styles.purchaseReference}>Referensi kebutuhan: <strong>{value.input.purchaseReference}</strong></p>}
      </section>
    </div><aside className={styles.summary} aria-label="Ringkasan biaya"><h3><ReceiptText size={20} aria-hidden="true" />Ringkasan biaya</h3>
      <dl className={styles.amounts}><div><dt>Subtotal barang</dt><dd>{orderAmount(value.quote.currency, value.quote.catalogSubtotal)}</dd></div><div><dt>Pajak</dt><dd>{orderAmount(value.quote.currency, value.tax)}</dd></div><div><dt>Biaya lain</dt><dd>{orderAmount(value.quote.currency, value.charges)}</dd></div></dl>
      <p className={styles.total}><strong>Total pesanan: {orderAmount(value.quote.currency, value.total)}</strong></p>
      <div className={styles.nextStep}>{reviewing ? children : <p>Nilai ini mengikuti rincian saat pesanan diajukan. Status persetujuan dan pemenuhan ditampilkan terpisah.</p>}</div>
    </aside></div>
  </>;
}
