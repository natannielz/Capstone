"use client";

import {useRef, type ReactNode, type RefObject} from "react";
import gsap from "gsap";
import {ScrollTrigger} from "gsap/ScrollTrigger";
import {useGSAP} from "@gsap/react";

if (typeof window !== "undefined") gsap.registerPlugin(useGSAP, ScrollTrigger);

type MotionScene = "landing" | "login";

/** Content and controls render usable before optional motion is attached. */
export function usePublicMotion(root: RefObject<HTMLElement | null>, scene: MotionScene, identity = "") {
  useGSAP(() => {
    const element = root.current;
    if (!element) return;
    const media = gsap.matchMedia();

    media.add({
      motion: "(prefers-reduced-motion: no-preference)",
      desktop: "(min-width: 960px)",
    }, context => {
      if (!context.conditions?.motion) return;
      const desktop = Boolean(context.conditions.desktop);
      const entrances: gsap.core.Animation[] = [];
      const one = (selector: string) => element.querySelector<HTMLElement>(selector);
      const all = (selector: string) => Array.from(element.querySelectorAll<HTMLElement>(selector));
      const sequence = gsap.timeline({defaults: {ease: "power2.out", clearProps: "transform,opacity"}});

      if (scene === "landing") {
        sequence.from(all(".home-title-line"), {y: desktop ? 24 : 14, opacity: 0.2, duration: 0.75, stagger: 0.09}, 0);
        sequence.from(all(".home-hero-copy > .home-eyebrow, .home-intro, .home-hero-note"), {y: 8, opacity: 0.55, duration: 0.6, stagger: 0.07}, 0.12);
        const image = one(".home-hero-photo img");
        if (image) sequence.from(image, {scale: 1.065, duration: 1.2}, 0);
        sequence.from(all(".home-product-preview img"), {scale: 0.96, opacity: 0.55, duration: 0.55, stagger: 0.08}, 0.2);

        // Text enters in reading order. Links, navigation and buttons stay still.
        for (const section of all(".home-access, .home-collections, .home-service")) {
          const targets = Array.from(section.querySelectorAll<HTMLElement>(".home-section-heading, .home-access-card > h3, .home-access-card > p, .home-service-copy > p, .home-service-copy > h2, .home-service li"));
          if (targets.length) entrances.push(gsap.from(targets, {
            y: desktop ? 20 : 12,
            opacity: 0.55,
            duration: 0.65,
            stagger: 0.055,
            ease: "power2.out",
            immediateRender: false,
            clearProps: "transform,opacity",
            scrollTrigger: {trigger: section, start: "top 87%", once: true},
          }));
        }
        for (const frame of all(".home-collection-image, .home-service-photo")) {
          const photo = frame.querySelector("img");
          if (photo) entrances.push(gsap.from(photo, {
            scale: 1.045,
            duration: 0.95,
            ease: "power2.out",
            immediateRender: false,
            clearProps: "transform",
            scrollTrigger: {trigger: frame, start: "top 90%", once: true},
          }));
        }
      } else {
        const image = one(".login-scene");
        if (image) sequence.from(image, {scale: 1.045, duration: 1.05}, 0);
        sequence.from(all(".login-story > span, .login-story > h1, .login-story > p"), {y: desktop ? 14 : 8, opacity: 0.45, duration: 0.65, stagger: 0.07}, 0.1);
        // No transforms on the form or its header: errors and credentials stay stable.
      }
      entrances.push(sequence);

      // These callbacks operate on existing animations only. No outer contextSafe
      // wrapper is invoked inside matchMedia's child context (which creates a cycle).
      const finishEntrances = () => entrances.forEach(animation => animation.progress(1));
      const refresh = () => ScrollTrigger.refresh();
      element.addEventListener("focusin", finishEntrances);
      const images = all("img") as HTMLImageElement[];
      images.forEach(image => image.addEventListener("load", refresh));
      let mounted = true;
      void document.fonts.ready.then(() => {if (mounted) refresh();});
      return () => {
        mounted = false;
        element.removeEventListener("focusin", finishEntrances);
        images.forEach(image => image.removeEventListener("load", refresh));
      };
    }, element);

    return () => media.revert();
  }, {scope: root, dependencies: [scene, identity], revertOnUpdate: true});
}

export function LandingMotion({children}: {children: ReactNode}) {
  const root = useRef<HTMLDivElement>(null);
  usePublicMotion(root, "landing");
  return <div ref={root} className="public-home public-motion">{children}</div>;
}
