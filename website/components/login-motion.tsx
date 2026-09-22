"use client";

import type {RefObject} from "react";
import {useGSAP} from "@gsap/react";
import gsap from "gsap";
import type {LoginPortal} from "@/lib/domain/navigation";

gsap.registerPlugin(useGSAP);

/** An entrance for each portal's story; every form control remains stationary. */
export function useLoginMotion(scope: RefObject<HTMLElement | null>, portal: LoginPortal) {
  useGSAP(() => {
    const media = gsap.matchMedia();
    media.add("(prefers-reduced-motion: no-preference)", () => {
      const root = scope.current;
      if (!root) return;
      const timeline = gsap.timeline({defaults: {ease: "power3.out"}});
      timeline.from(root.querySelectorAll("[data-login-copy]"), {
        y: 16, opacity: 0.45, duration: 0.65, stagger: 0.08,
        clearProps: "transform,opacity",
      }, 0);
      if (portal === "customer") {
        timeline.from(root.querySelectorAll("[data-login-art]"), {
          y: 26, scale: 0.975, opacity: 0.6, duration: 0.9,
          clearProps: "transform,opacity",
        }, 0.1);
      } else {
        timeline.from(root.querySelectorAll("[data-login-path]"), {
          scaleY: 0, transformOrigin: "center top", duration: 0.65, stagger: 0.13,
          clearProps: "transform,transformOrigin",
        }, 0.12);
        timeline.from(root.querySelectorAll("[data-login-step]"), {
          x: -10, opacity: 0.3, duration: 0.55, stagger: 0.06,
          clearProps: "transform,opacity",
        }, 0.18);
      }
      // This callback only settles the existing timeline: it creates no nested context.
      const settle = () => { timeline.progress(1); };
      root.addEventListener("focusin", settle);
      return () => root.removeEventListener("focusin", settle);
    }, scope);
    return () => media.revert();
  }, {scope, dependencies: [portal], revertOnUpdate: true});
}
