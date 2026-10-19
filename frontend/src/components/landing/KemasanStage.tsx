'use client';

import { useEffect, useRef, useState } from "react";
import { KemasanPoster } from "./KemasanPoster";
import { readSceneEnvironment, sceneMode } from "./scene-mode";
import type { KemasanScene } from "./kemasan-scene";
import styles from "./landing.module.css";

/**
 * Decorative stage, hidden from assistive technology (the label beside it states the same thing as text). The server
 * renders the poster; three.js loads in its own chunk after hydration, only where sceneMode allows it, and the poster
 * comes back if the WebGL context is lost.
 */
export function KemasanStage({ cartons }: { cartons: number }) {
  const canvasRef = useRef<HTMLCanvasElement>(null), sceneRef = useRef<KemasanScene | null>(null), cartonsRef = useRef(cartons);
  const [ready, setReady] = useState(false);
  useEffect(() => {
    const canvas = canvasRef.current, mode = sceneMode(readSceneEnvironment(window));
    if (!canvas || mode === "none") return;
    let cancelled = false, scene: KemasanScene | null = null;
    void import("./kemasan-scene").then(async ({ createKemasanScene }) => {
      if (cancelled) return;
      const stencil = getComputedStyle(document.documentElement).getPropertyValue("--font-stencil").trim() || "sans-serif";
      scene = await createKemasanScene(canvas, { mode, cartons: cartonsRef.current, stencil, onLost: () => setReady(false) });
      if (cancelled) { scene.dispose(); return; }
      sceneRef.current = scene; setReady(true);
    }).catch(() => { /* The poster stays: the scene is decorative. */ });
    return () => { cancelled = true; scene?.dispose(); sceneRef.current = null; };
  }, []);
  useEffect(() => { cartonsRef.current = cartons; sceneRef.current?.setCartons(cartons); }, [cartons]);
  return <div className={styles.stage} aria-hidden="true" data-stage="kemasan" data-ready={ready || undefined}>
    <KemasanPoster cartons={cartons} className={styles.poster} />
    <canvas ref={canvasRef} className={styles.canvas} />
  </div>;
}
