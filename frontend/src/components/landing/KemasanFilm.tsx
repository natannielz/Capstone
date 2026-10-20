/**
 * The story as a 24-second film, rendered locally from the same scene (scripts/render-landing-film.mjs). It plays only
 * when the visitor presses play, so it respects reduced motion; the paragraph describes what it shows.
 */
export function KemasanFilm({ className }: { className?: string }) {
  return <section className={className} aria-labelledby="judul-film">
    <div>
      <h2 id="judul-film">Satu kardus, 24 detik.</h2>
      <p id="isi-film">Barang yang diminta masuk satu per satu dan diperiksa, kardus ditutup, dilakban dan diberi label, lalu diantar ke meja cabang, tempat tanda terima dicap dan tagihan terbit.</p>
    </div>
    <video controls preload="none" playsInline poster="/media/satu-kardus-poster.jpg" aria-describedby="isi-film" width={1280} height={720}>
      <source src="/media/satu-kardus.webm" type="video/webm" />
    </video>
  </section>;
}
