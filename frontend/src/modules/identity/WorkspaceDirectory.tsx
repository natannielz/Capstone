'use client';

import Link from 'next/link';
import { useState } from 'react';
import { workspaceDirectory } from './workspace-directory';
import styles from './identity.module.css';

export function WorkspaceDirectory({ actions, accessState }: { actions: readonly string[]; accessState: 'denied' | 'unavailable' | null }) {
  const [query, setQuery] = useState('');
  const available = accessState === null ? workspaceDirectory(actions) : [], groups = accessState === null ? workspaceDirectory(actions, query) : [];
  return <section className={styles.workspace} aria-labelledby="workspace-heading">
    <div className={styles.directoryHeader}><div><h2 id="workspace-heading" tabIndex={-1}>Ruang kerja tersedia</h2><p className={styles.help}>Pilih tugas sesuai penugasan akun Anda. Cakupan dan tindakan diperiksa kembali di setiap ruang kerja.</p></div><a href="#profile-heading" className={styles.accountLink}>Sesi dan verifikasi akun ↓</a></div>
    {accessState !== null ? <p className={styles.alert} role="status">{accessState === 'denied' ? 'Akses ke daftar tugas belum diizinkan untuk sesi ini. Hubungi pengelola organisasi, atau gunakan “Periksa sesi” setelah akses diperbarui.' : 'Daftar tugas belum dapat dimuat. Gunakan “Periksa sesi” untuk mencoba kembali.'}</p>
      : available.length === 0 ? <p className={styles.help} role="status">Belum ada ruang kerja yang tersedia untuk penugasan akun ini. Hubungi pengelola organisasi jika Anda memerlukan akses.</p>
      : <><div className={styles.directorySearch}><label htmlFor="workspace-search">Cari tugas<input id="workspace-search" type="search" maxLength={120} autoComplete="off" placeholder="Misalnya pesanan, pemasok atau tagihan" value={query} onChange={event => setQuery(event.target.value)} aria-describedby="workspace-search-help" /></label>{query && <button type="button" className={styles.secondary} onClick={() => setQuery('')}>Hapus pencarian</button>}</div><p id="workspace-search-help" className={styles.help}>Pencarian hanya menampilkan tugas yang tersedia untuk akun ini.</p>
        {groups.length === 0 ? <p role="status" className={styles.emptyDirectory}>Tidak ada tugas yang cocok. Coba kata lain atau hapus pencarian untuk melihat tugas yang tersedia.</p> : <><nav className={styles.workspaceSections} aria-label="Bidang kerja">{groups.map(group => <a key={group.id} href={'#workspace-' + group.id}>{group.label}</a>)}</nav><nav className={styles.directoryGroups} aria-label="Ruang kerja akun">{groups.map(group => <section className={styles.directoryGroup} key={group.id} aria-labelledby={'workspace-' + group.id}><header><h3 id={'workspace-' + group.id} tabIndex={-1}>{group.label}</h3><p className={styles.help}>{group.description}</p></header><div className={styles.destinationGrid}>{group.items.map(item => <Link prefetch={false} href={item.href} key={item.href} className={styles.destination}><h4 className={styles.destinationTitle}>{item.label}<span aria-hidden="true">↗</span></h4><p>{item.description}</p></Link>)}</div></section>)}</nav></>}
      </>}
  </section>;
}
