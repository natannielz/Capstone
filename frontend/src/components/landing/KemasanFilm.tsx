/**
 * The story as a short film (an illustration, not a promise of timing), rendered locally from the same scene (scripts/render-landing-film.mjs). It plays only
 * when the visitor presses play, so it respects reduced motion; the paragraph describes what it shows.
 */
export function KemasanFilm({ className }: { className?: string }) {
  return <section className={className} aria-labelledby="judul-film">
    <div>
      <h2 id="judul-film">Lihat satu permintaan berjalan.</h2>
      <p id="isi-film">Ilustrasi alurnya: barang yang diminta diperiksa dan dikemas dalam satu kardus, kardus ditutup dan diberi label, lalu diantar ke meja cabang, tempat tanda terima dicatat dan tagihan terbit.</p>
      <p className="film-note">Lama setiap langkah mengikuti jadwal peninjauan toko dan pengiriman ke cabang Anda.</p>
    </div>
    <video controls preload="none" playsInline poster="/media/satu-kardus-poster.jpg" aria-describedby="isi-film" width={1280} height={720}>
      <source src="/media/satu-kardus.webm" type="video/webm" />
    </video>
  </section>;
}
