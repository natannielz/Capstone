"use client";

import Link from "next/link";
import { ArrowLeft, ArrowRight, Pause, Play } from "lucide-react";
import useEmblaCarousel from "embla-carousel-react";
import { useCallback, useEffect, useId, useRef, useState, useSyncExternalStore } from "react";
import { Art } from "./art";

export type CollectionSlide = {
  id: string;
  title: string;
  description: string;
  image: string;
  href: string;
  linkLabel?: string;
};

export const DEFAULT_COLLECTION_SLIDES: CollectionSlide[] = [
  { id: "pantry", title: "Persediaan untuk jeda yang nikmat.", description: "Kopi, teh, dan camilan untuk menemani kegiatan sehari-hari.", image: "/images/products/coffee.png", href: "/shop?collection=pantry", linkLabel: "Lihat pantry harian" },
  { id: "rapat", title: "Siapkan meja. Mulai pertemuan.", description: "Minuman dan konsumsi rapat, dalam kemasan sesuai kebutuhan tim.", image: "/images/products/snack.png", href: "/shop?collection=rapat", linkLabel: "Lihat kebutuhan rapat" },
  { id: "merchandise", title: "Pilihan yang ikut ke mana saja.", description: "Tumbler, tas, dan merchandise untuk melengkapi kegiatan Anda.", image: "/images/products/tumbler.png", href: "/shop?collection=merchandise", linkLabel: "Lihat merchandise" },
];

const AUTOPLAY_DELAY = 6000;
const MAX_TRANSITION = 650;
const MOTION_QUERY = "(prefers-reduced-motion: reduce)";
function subscribeMotion(callback: () => void) {
  const query = window.matchMedia(MOTION_QUERY);
  query.addEventListener("change", callback);
  return () => query.removeEventListener("change", callback);
}
function subscribeVisibility(callback: () => void) {
  document.addEventListener("visibilitychange", callback);
  return () => document.removeEventListener("visibilitychange", callback);
}

/** Collection discovery only: product search, prices and purchase controls stay stationary. */
export function CollectionCarousel({
  slides = DEFAULT_COLLECTION_SLIDES,
  ariaLabel = "Pilihan kebutuhan",
  className = "",
}: {
  slides?: CollectionSlide[];
  ariaLabel?: string;
  className?: string;
}) {
  const root = useRef<HTMLElement>(null);
  const viewportElement = useRef<HTMLDivElement>(null);
  const settleTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const pointerRotationIntent = useRef<boolean | undefined>(undefined);
  const gestureStart = useRef<{x: number; y: number; id: number; dragged: boolean} | null>(null);
  const labelId = useId();
  const viewportId = useId();
  const motionNoteId = useId();
  const reducedMotion = useSyncExternalStore(subscribeMotion, () => window.matchMedia(MOTION_QUERY).matches, () => true);
  const documentVisible = useSyncExternalStore(subscribeVisibility, () => document.visibilityState === "visible", () => false);
  const [inView, setInView] = useState(false);
  const [controlHovered, setControlHovered] = useState(false);
  const [resumeOverControl, setResumeOverControl] = useState(false);
  const [pointerHeld, setPointerHeld] = useState(false);
  const [requestedRotation, setRequestedRotation] = useState(true);
  const [selected, setSelected] = useState(0);
  const [snapCount, setSnapCount] = useState(0);
  const [engineRevision, setEngineRevision] = useState(0);
  const [cycle, setCycle] = useState(0);
  const [viewportRef, api] = useEmblaCarousel({
    align: "start",
    loop: slides.length > 1,
    duration: reducedMotion ? 0 : 20,
    watchDrag: slides.length > 1 && !reducedMotion,
    watchFocus: false,
  });
  const attachViewport = useCallback((element: HTMLDivElement | null) => {
    viewportElement.current = element;
    viewportRef(element);
  }, [viewportRef]);
  const current = Math.min(selected, Math.max(0, slides.length - 1));
  const ready = Boolean(api && snapCount > 1);
  const hovering = controlHovered && !resumeOverControl;
  const rotating = ready && requestedRotation && !reducedMotion && documentVisible && inView && !hovering && !pointerHeld;
  const rotationStatus = !ready ? "Menyiapkan koleksi" : reducedMotion ? "Gerakan dikurangi" : !requestedRotation ? "Dijeda" : hovering || pointerHeld ? "Jeda saat memilih" : !documentVisible || !inView ? "Otomatis saat terlihat" : "Bergeser otomatis";

  const stopRotation = useCallback(() => setRequestedRotation(false), []);
  const clearSettleTimer = useCallback(() => {
    if (settleTimer.current !== undefined) clearTimeout(settleTimer.current);
    settleTimer.current = undefined;
  }, []);

  useEffect(() => {
    const release = () => { gestureStart.current = null; setPointerHeld(false); };
    const finish = (event: PointerEvent) => { if (gestureStart.current?.id === event.pointerId) release(); };
    const track = (event: PointerEvent) => {
      const start = gestureStart.current;
      if (!start || start.id !== event.pointerId || start.dragged) return;
      const dx = Math.abs(event.clientX - start.x), dy = Math.abs(event.clientY - start.y);
      if (dx > 10 && dx > dy) { start.dragged = true; stopRotation(); clearSettleTimer(); }
    };
    window.addEventListener("pointermove", track, true);
    window.addEventListener("pointerup", finish, true);
    window.addEventListener("pointercancel", finish, true);
    window.addEventListener("blur", release);
    return () => {
      window.removeEventListener("pointermove", track, true);
      window.removeEventListener("pointerup", finish, true);
      window.removeEventListener("pointercancel", finish, true);
      window.removeEventListener("blur", release);
    };
  }, [stopRotation, clearSettleTimer]);

  useEffect(() => {
    const element = viewportElement.current;
    if (!element || !slides.length) return;
    const observer = new IntersectionObserver((entries) => {
      const entry = entries[entries.length - 1];
      setInView(entry.isIntersecting && entry.intersectionRatio >= 0.05);
    }, { threshold: [0, 0.05] });
    observer.observe(element);
    return () => observer.disconnect();
  }, [slides.length]);

  useEffect(() => {
    if (!api) return;
    const sync = () => { setSelected(api.selectedScrollSnap()); setSnapCount(api.scrollSnapList().length); };
    const reinitialize = () => { clearSettleTimer(); sync(); setEngineRevision(value => value + 1); };
    api.on("select", sync).on("reInit", reinitialize).on("settle", clearSettleTimer);
    const initialSync = requestAnimationFrame(reinitialize);
    return () => {
      cancelAnimationFrame(initialSync);
      clearSettleTimer();
      api.off("select", sync).off("reInit", reinitialize).off("settle", clearSettleTimer);
    };
  }, [api, clearSettleTimer]);

  const move = useCallback((direction: -1 | 1) => {
    if (!api || api.scrollSnapList().length < 2) return;
    clearSettleTimer();
    const count = api.scrollSnapList().length;
    const target = (api.selectedScrollSnap() + direction + count) % count;
    // Embla's duration is physics-based, not milliseconds; cap programmatic settling.
    api.scrollTo(target, reducedMotion);
    if (!reducedMotion) settleTimer.current = setTimeout(() => { api.scrollTo(target, true); settleTimer.current = undefined; }, MAX_TRANSITION);
  }, [api, clearSettleTimer, reducedMotion]);

  useEffect(() => {
    if (!rotating) return;
    // Re-arm even if a resize temporarily prevents the selected snap changing.
    let timer: ReturnType<typeof setTimeout>;
    const advance = () => {
      move(1);
      setCycle(value => value + 1);
      timer = setTimeout(advance, AUTOPLAY_DELAY);
    };
    timer = setTimeout(advance, AUTOPLAY_DELAY);
    return () => clearTimeout(timer);
  }, [rotating, engineRevision, move]);

  if (slides.length === 0) return null;

  return (
    <section
      ref={root}
      className={`collection-carousel ${className}`.trim()}
      aria-roledescription="carousel"
      aria-labelledby={labelId}
      data-rotating={rotating ? "true" : "false"}
      onFocusCapture={() => {
        stopRotation();
        clearSettleTimer();
        // A newly focused link must not continue moving under the user's focus.
        api?.scrollTo(api.selectedScrollSnap(), true);
      }}
      onPointerOver={(event) => {
        if (event.pointerType !== "mouse" && event.pointerType !== "pen") return;
        const interactive = event.target instanceof Element && Boolean(event.target.closest("a,button"));
        setControlHovered(interactive);
        if (!interactive) setResumeOverControl(false);
      }}
      onPointerLeave={() => { setControlHovered(false); setResumeOverControl(false); }}
      onPointerDownCapture={(event) => {
        if (!event.isPrimary || event.button !== 0 || !(event.target instanceof Element) || !viewportElement.current?.contains(event.target)) return;
        gestureStart.current = {x: event.clientX, y: event.clientY, id: event.pointerId, dragged: false};
        setPointerHeld(true);
        clearSettleTimer();
      }}
    >
      <div className="collection-carousel-toolbar">
        <div className="collection-carousel-heading"><p id={labelId} className="collection-carousel-label">{ariaLabel}</p>{slides.length > 1 && <span className="collection-carousel-status">{rotationStatus}</span>}</div>
        {slides.length > 1 && (
          <div className="collection-carousel-controls">
            <button
              type="button"
              className="collection-carousel-rotation"
              disabled={!ready || reducedMotion}
              aria-label={requestedRotation && !reducedMotion ? "Jeda pergantian koleksi otomatis" : "Mulai pergantian koleksi otomatis"}
              title={requestedRotation && !reducedMotion ? "Jeda pergantian otomatis" : "Lanjutkan pergantian otomatis"}
              aria-describedby={reducedMotion ? motionNoteId : undefined}
              aria-controls={viewportId}
              onPointerDown={() => { pointerRotationIntent.current = !requestedRotation; }}
              onPointerCancel={() => { pointerRotationIntent.current = undefined; }}
              onKeyDown={() => { pointerRotationIntent.current = undefined; }}
              onClick={() => {
                const resume = pointerRotationIntent.current ?? !requestedRotation;
                setRequestedRotation(resume);
                setResumeOverControl(resume);
                pointerRotationIntent.current = undefined;
              }}
            >
              {requestedRotation && !reducedMotion ? <Pause size={15} aria-hidden="true" /> : <Play size={15} aria-hidden="true" />}
            </button>
            <span className="collection-carousel-count" aria-hidden="true">{String(current + 1).padStart(2, "0")} <span>/ {String(slides.length).padStart(2, "0")}</span></span>
            <button type="button" className="collection-carousel-arrow" disabled={!api} aria-label="Koleksi sebelumnya" aria-controls={viewportId} onClick={() => { stopRotation(); move(-1); }}><ArrowLeft size={18} aria-hidden="true" /></button>
            <button type="button" className="collection-carousel-arrow" disabled={!api} aria-label="Koleksi berikutnya" aria-controls={viewportId} onClick={() => { stopRotation(); move(1); }}><ArrowRight size={18} aria-hidden="true" /></button>
          </div>
        )}
      </div>
      <div className="collection-carousel-progress" aria-hidden="true"><span key={`${cycle}-${engineRevision}-${rotating}`} style={{animationDuration: `${AUTOPLAY_DELAY}ms`}} /></div>
      {reducedMotion && <span id={motionNoteId} className="collection-carousel-sr">Pergantian otomatis dinonaktifkan sesuai pengaturan kurangi gerakan. Gunakan tombol koleksi sebelumnya atau berikutnya.</span>}
      <div ref={attachViewport} id={viewportId} className="collection-carousel-viewport">
        <div className="collection-carousel-track" aria-live={rotating ? "off" : "polite"} aria-atomic="false">
          {slides.map((slide, index) => (
            <div key={slide.id} className="collection-carousel-slide" role="group" aria-roledescription="slide" aria-label={`${index + 1} dari ${slides.length}: ${slide.title}`} aria-hidden={index !== current} inert={index !== current}>
              <div className="collection-carousel-card">
                <div className="collection-carousel-copy">
                  <h2>{slide.title}</h2>
                  <p>{slide.description}</p>
                  <Link href={slide.href} className="collection-carousel-link" tabIndex={index === current ? 0 : -1}>{slide.linkLabel || "Lihat koleksi"}<ArrowRight size={18} aria-hidden="true" /></Link>
                </div>
                <div className="collection-carousel-photo"><Art src={slide.image} alt="" loading={index === 0 ? "eager" : "lazy"} sizes="(max-width: 640px) 85vw, 40vw" /></div>
              </div>
            </div>
          ))}
        </div>
      </div>
    </section>
  );
}
