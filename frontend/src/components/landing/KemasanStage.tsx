'use client';

import { useEffect, useRef, useState } from "react";
import { KemasanPoster } from "./KemasanPoster";
import { readSceneEnvironment, sceneMode } from "./scene-mode";
import type { KemasanScene } from "./kemasan-scene";
import styles from "./landing.module.css";

const cssFont = (name: string, fallback: string) => getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;

/**
 * Decorative stage, hidden from assistive technology (the label and the act cards state the same things as text). The
 * server renders the poster; three.js loads in its own chunk after hydration, only where sceneMode allows it, and the
 * poster comes back if the WebGL context is lost. The journey wrapper drives the scene through `onScene`.
 */
export function KemasanStage({ cartons, acts, onScene }: { cartons: number; acts: number; onScene: (scene: KemasanScene | null) => void }) {
  const canvasRef = useRef<HTMLCanvasElement>(null), sceneRef = useRef<KemasanScene | null>(null), cartonsRef = useRef(cartons), onSceneRef = useRef(onScene);
  const [ready, setReady] = useState(false);
  useEffect(() => { onSceneRef.current = onScene; }, [onScene]);
  useEffect(() => {
    const canvas = canvasRef.current, mode = sceneMode(readSceneEnvironment(window));
    if (!canvas || mode === "none") return;
    let cancelled = false, scene: KemasanScene | null = null;
    void import("./kemasan-scene").then(async ({ createKemasanScene }) => {
      if (cancelled) return;
      const fonts = { stencil: cssFont("--font-stencil", "sans-serif"), body: cssFont("--font-body", "sans-serif") };
      scene = await createKemasanScene(canvas, { mode, cartons: cartonsRef.current, acts, fonts, onLost: () => { setReady(false); onSceneRef.current(null); } });
      if (cancelled) { scene.dispose(); return; }
      sceneRef.current = scene; setReady(true); onSceneRef.current(scene);
    }).catch(() => { /* The poster stays: the scene is decorative. */ });
    return () => { cancelled = true; onSceneRef.current(null); scene?.dispose(); sceneRef.current = null; };
  }, [acts]);
  useEffect(() => { cartonsRef.current = cartons; sceneRef.current?.setCartons(cartons); }, [cartons]);
  return <div className={styles.stage} aria-hidden="true" data-stage="kemasan" data-ready={ready || undefined}>
    <KemasanPoster cartons={cartons} className={styles.poster} />
    <canvas ref={canvasRef} className={styles.canvas} />
  </div>;
}
