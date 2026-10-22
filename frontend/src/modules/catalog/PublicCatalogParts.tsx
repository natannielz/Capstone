import Link from 'next/link';
import { PackageOpen } from 'lucide-react';
import { storeSignInHref } from '@/components/store/store-navigation';
import { formatNumber } from '@/lib/format';
import type { PublicCatalogItem, PublicCatalogPackage, PublicCatalogQuery } from './public-catalog-client';
import { buttonClassName } from '@/components/workspace-kit/button';
import { StateView } from '@/components/workspace-kit/state-view';
import catalog from './storefront-catalog.module.css';
import styles from './public-catalog.module.css';

/** Where a signed-in visitor continues: the organisation catalog with the same search, category and order (never the page
 * number), or the package they were looking at (the one in the address, else the product's first). When the product is missing
 * or not a guest sample, the id from the address is kept, so a member following a shared package link still lands on it. */
export function signedInEquivalent(item: PublicCatalogItem | null, publicId?: string, query?: PublicCatalogQuery): string {
  if (item) {
    const selected = item.packages.find(entry => entry.publicId === publicId) ?? item.packages[0];
    return '/shop/' + selected.publicId;
  }
  if (publicId) return '/shop/' + publicId;
  const parameters = new URLSearchParams();
  const search = query?.search?.trim();
  if (search) parameters.set('q', search);
  if (query?.category) parameters.set('kategori', query.category);
  if (query?.sort === 'name') parameters.set('urut', 'nama');
  const suffix = parameters.toString();
  return '/shop' + (suffix ? '?' + suffix : '');
}

/** Sign-in returns only to canonical paths (the identity layer refuses a query string), so a link keeps the page itself;
 * the redirect of a visitor who is already signed in keeps the query. */
export function guestSignInHref(target: string) { return storeSignInHref(target.split('?')[0]); }

/** Only a visitor who may read the organisation catalog is sent to it; anyone else stays on the samples. */
export function memberRedirect(session: { authenticated: boolean; account: unknown }, actions: readonly string[], target: string): string | null {
  return session.authenticated && session.account && actions.includes('catalog.read') ? target : null;
}

export function packLine(item: Pick<PublicCatalogPackage, 'packageLabel' | 'baseUnitsPerPackage' | 'baseUnitCode'>) {
  return item.baseUnitsPerPackage > 1 && !/\bisi\b/i.test(item.packageLabel) ? `${item.packageLabel} · isi ${item.baseUnitsPerPackage} ${item.baseUnitCode}` : item.packageLabel;
}

export function GuestSampleBanner({ target }: { target: string }) {
  return <p className={styles.gateNote} role="note">Contoh barang. Masuk untuk melihat seluruh katalog dan harga cabang Anda.{' '}
    <Link href={guestSignInHref(target)}>Masuk</Link></p>;
}

export function MemberCatalogNote() {
  return <p className={styles.gateNote} role="note">Akun Anda tidak mencakup katalog organisasi, jadi Anda melihat contoh barang untuk tamu.{' '}
    <Link href="/identity/account">Periksa akun</Link></p>;
}

export function GuestSampleEmpty({ target }: { target: string }) {
  return <StateView kind="empty" align="center" icon={PackageOpen} title="Contoh katalog belum dipilih" message="Akun cabang tetap dapat melihat seluruh katalog dan harganya setelah masuk."
    action={<Link className={buttonClassName({ variant: 'primary' })} href={guestSignInHref(target)}>Masuk</Link>} />;
}

/** The same answer for an unknown id and for a product that is not a guest sample. Only the id already in the address is used
 * (for the sign-in return), so the page never hints that the product exists. */
export function GuestUnavailable({ publicId }: { publicId: string }) {
  return <StateView kind="empty" align="center" icon={PackageOpen} title="Barang ini tidak tersedia untuk tamu." message="Lihat contoh barang lainnya, atau masuk dengan akun cabang Anda."
    action={<Link className={buttonClassName({ variant: 'primary' })} href="/katalog">Lihat contoh katalog</Link>}
    secondaryAction={<Link className={buttonClassName({ variant: 'secondary' })} href={guestSignInHref(signedInEquivalent(null, publicId))}>Masuk</Link>} />;
}

export function GuestProductCard({ item }: { item: PublicCatalogItem }) {
  const single = item.packages.length === 1 ? item.packages[0] : null;
  return <li className={catalog.card}>
    <Link href={'/katalog/' + item.publicId} className={styles.cardLink}>
      <span className={catalog.media}><PackageOpen size={40} strokeWidth={1.2} aria-hidden="true" /></span>
      <span className={catalog.cardBody}>
        <span className={catalog.category}>{item.categoryName}</span>
        <span className={catalog.name}>{item.name}</span>
        <span className={catalog.meta}>{single ? packLine(single) : `${formatNumber(item.packages.length)} kemasan`}</span>
        {single && single.minimumOrderPackages > 1 && <span className={styles.minimum}>Min. {single.minimumOrderPackages} kemasan</span>}
      </span>
    </Link>
    <Link className={styles.priceGate} href={guestSignInHref(signedInEquivalent(item))}>Masuk untuk lihat harga</Link>
  </li>;
}
