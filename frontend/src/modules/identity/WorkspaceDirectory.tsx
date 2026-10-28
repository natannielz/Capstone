'use client';

import Link from 'next/link';
import { useState } from 'react';
import { Button, buttonClassName } from '@/components/workspace-kit/button';
import { Field } from '@/components/workspace-kit/field';
import { Section } from '@/components/workspace-kit/section';
import { StateView } from '@/components/workspace-kit/state-view';
import { workspaceDirectory } from './workspace-directory';
import styles from './workspace-home.module.css';

export function WorkspaceDirectory({ actions, accessState }: { actions: readonly string[]; accessState: 'denied' | 'unavailable' | null }) {
  const [query, setQuery] = useState('');
  const available = accessState === null ? workspaceDirectory(actions) : [], groups = accessState === null ? workspaceDirectory(actions, query) : [];
  return <Section title="Ruang kerja tersedia" id="workspace-heading" variant="plain"
    description="Pilih tugas sesuai penugasan akun Anda. Cakupan dan tindakan diperiksa kembali di setiap ruang kerja."
    actions={<a href="/identity/account#profile-heading" className={buttonClassName({ variant: 'secondary' })}>Sesi dan verifikasi akun</a>}>
    {accessState !== null ? <StateView kind={accessState === 'denied' ? 'denied' : 'error'}
      title={accessState === 'denied' ? 'Akses ke daftar tugas belum diizinkan untuk sesi ini. Hubungi pengelola organisasi, atau gunakan “Muat ulang sesi dan akses” setelah akses diperbarui.' : 'Daftar tugas belum dapat dimuat. Gunakan “Muat ulang sesi dan akses” untuk mencoba kembali.'} />
      : available.length === 0 ? <StateView kind="empty" title="Belum ada ruang kerja yang tersedia untuk penugasan akun ini. Hubungi pengelola organisasi jika Anda memerlukan akses." />
      : <>
        <div className={styles.search}>
          <Field id="workspace-search" label="Cari tugas" width="search" help="Pencarian hanya menampilkan tugas yang tersedia untuk akun ini.">
            <input type="search" maxLength={120} autoComplete="off" placeholder="Misalnya pesanan, pemasok atau tagihan" value={query} onChange={event => setQuery(event.target.value)} /></Field>
          {query && <Button onClick={() => setQuery('')}>Hapus pencarian</Button>}
        </div>
        {groups.length === 0 ? <StateView kind="empty" title="Tidak ada tugas yang cocok. Coba kata lain atau hapus pencarian untuk melihat tugas yang tersedia." />
          : <>
            <nav className={styles.chips} aria-label="Bidang kerja">{groups.map(group => <a key={group.id} href={'#workspace-' + group.id}>{group.label}</a>)}</nav>
            <nav className={styles.groups} aria-label="Ruang kerja akun">{groups.map(group => <section className={styles.group} key={group.id} aria-labelledby={'workspace-' + group.id}>
              <header><h3 id={'workspace-' + group.id} tabIndex={-1}>{group.label}</h3><p className={styles.help}>{group.description}</p></header>
              <div className={styles.grid}>{group.items.map(item => <Link prefetch={false} href={item.href} key={item.href} className={styles.destination}>
                <h4 className={styles.destinationTitle}>{item.label}</h4><p>{item.description}</p></Link>)}</div>
            </section>)}</nav>
          </>}
      </>}
  </Section>;
}
