'use client';

import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { activeAct, progressFromAnchors } from "./journey";
import { DEMO_MAXIMUM, DEMO_MINIMUM, KemasanLabel } from "./KemasanLabel";
import { KemasanStage } from "./KemasanStage";
import type { KemasanScene } from "./kemasan-scene";
import styles from "./landing.module.css";

export type JourneyAct = { status: string; time: string; text: string; detail: string };

/** Typed text that is not a whole number in range keeps the stage on the nearest valid count. */
function cartonCount(text: string): number {
  const typed = /^[0-9]{1,2}$/.test(text.trim()) ? Number(text.trim()) : DEMO_MINIMUM;
  return Math.min(DEMO_MAXIMUM, Math.max(DEMO_MINIMUM, typed));
}

/**
 * The hero and the story share one pinned stage. Scrolling moves the scene through the acts: the hero copy's bottom
 * edge and each act card's centre are anchors, and an act is reached when its anchor rises to the focus line.
 */
export function KemasanJourney({ acts, children }: { acts: readonly JourneyAct[]; children: ReactNode }) {
  const [value, setValue] = useState(String(DEMO_MINIMUM)), [current, setCurrent] = useState(0);
  const cartons = cartonCount(value);
  const sceneRef = useRef<KemasanScene | null>(null), copyRef = useRef<HTMLDivElement>(null), actRefs = useRef<(HTMLLIElement | null)[]>([]);
  const progressRef = useRef(0);

  const measure = useCallback(() => {
    const copy = copyRef.current; if (!copy) return;
    const phone = window.matchMedia("(max-width: 900px)").matches, focus = window.innerHeight * (phone ? 0.76 : 0.55);
    const anchors = [copy.getBoundingClientRect().bottom, ...actRefs.current.slice(0, acts.length).map(card => {
      const box = card?.getBoundingClientRect(); return box ? box.top + box.height / 2 : Infinity;
    })];
    const progress = progressFromAnchors(anchors, focus);
    progressRef.current = progress; sceneRef.current?.setJourney(progress);
    setCurrent(previous => previous === activeAct(progress) ? previous : activeAct(progress));
  }, [acts.length]);

  useEffect(() => {
    let request = 0;
    const schedule = () => { if (!request) request = requestAnimationFrame(() => { request = 0; measure(); }); };
    schedule();
    window.addEventListener("scroll", schedule, { passive: true }); window.addEventListener("resize", schedule);
    return () => { cancelAnimationFrame(request); window.removeEventListener("scroll", schedule); window.removeEventListener("resize", schedule); };
  }, [measure]);

  const onScene = useCallback((scene: KemasanScene | null) => { sceneRef.current = scene; scene?.setJourney(progressRef.current); }, []);

  return <div className={styles.journey} data-journey={acts.length} data-act={current}>
    <div className={styles.copy} ref={copyRef}>{children}</div>
    <div className={styles.stageColumn}><KemasanStage cartons={cartons} acts={acts.length} onScene={onScene} /></div>
    <div className={styles.labelSlot}><KemasanLabel value={value} cartons={cartons} onChange={setValue} /></div>
    <div className={styles.storyIntro}>
      <h2>Satu karton, dari dok ke cabang.</h2>
      <p>Ikuti satu permintaan dari pengajuan sampai {acts.length > 2 ? "tagihan terbit" : "hasil peninjauan toko"}. Setiap tahap di bawah adalah status yang juga Anda lihat di akun.</p>
    </div>
    <ol className={styles.acts} aria-label="Perjalanan satu permintaan">
      {acts.map((act, index) => <li key={act.status} data-step={act.status} data-current={current === index + 1 || undefined}
        ref={element => { actRefs.current[index] = element; }} className={styles.act}>
        <span className={styles.actNumber} aria-hidden="true">{String(index + 1).padStart(2, "0")}</span>
        <div className={styles.actBody}>
          <p className={styles.actTime} data-act-time="">{act.time} WIB</p>
          <h3>{act.status}</h3>
          <p>{act.text}</p>
          <p className={styles.actDetail}>{act.detail}</p>
        </div>
      </li>)}
    </ol>
  </div>;
}
