"use client";

import {useRef, type ReactNode, type RefObject} from "react";
import gsap from "gsap";
import {ScrollTrigger} from "gsap/ScrollTrigger";
import {useGSAP} from "@gsap/react";

if (typeof window !== "undefined") gsap.registerPlugin(useGSAP, ScrollTrigger);

type MotionScene = "landing" | "login";

/** All content renders visibly; motion is added only after the page is usable. */
export function usePublicMotion(root: RefObject<HTMLElement | null>, scene: MotionScene, identity = "") {
  useGSAP((_context, contextSafe) => {
    const element = root.current;
    if (!element || !contextSafe) return;
    const media = gsap.matchMedia();

    media.add({
      motion: "(prefers-reduced-motion: no-preference)",
      desktop: "(min-width: 960px)",
      pointer: "(hover: hover) and (pointer: fine)",
    }, context => {
      if (!context.conditions?.motion) return;
      const desktop = Boolean(context.conditions.desktop);
      const entrances: gsap.core.Animation[] = [];
      const listeners: (() => void)[] = [];
      const one = (selector: string) => element.querySelector<HTMLElement>(selector);
      const all = (selector: string) => Array.from(element.querySelectorAll<HTMLElement>(selector));

      if (scene === "landing") {
        const headline = all(".home-title-line");
        const hero = one(".home-hero");
        const image = one(".home-hero-visual > img");
        const sequence = gsap.timeline({defaults: {ease: "power3.out"}});
        sequence.from(headline, {yPercent: 108, rotation: desktop ? 3 : 0, transformOrigin: "left bottom", duration: 1.05, stagger: 0.12, clearProps: "transform,opacity,transformOrigin"}, 0);
        sequence.from(all(".home-hero-copy > .home-eyebrow, .home-intro, .home-hero-note"), {y: 10, opacity: 0.6, duration: 0.62, stagger: 0.08, clearProps: "transform,opacity"}, 0.12);
        const frame = one(".home-hero-visual");
        if (frame) sequence.from(frame, {clipPath: "inset(14% 7% 14% 7% round 160px 160px 12px 12px)", duration: 1.25, clearProps: "clipPath"}, 0);
        if (image) sequence.fromTo(image, {scale: 1.2}, {scale: 1.06, duration: 1.65, ease: "power2.out"}, 0);
        sequence.from(all(".home-floating-card"), {y: 75, rotation: 0, scale: 0.8, opacity: 0, stagger: 0.14, duration: 1.1, ease: "back.out(1.25)", clearProps: "transform,opacity"}, 0.3);
        entrances.push(sequence);

        // Brief, single entrances. No content is hidden while waiting to scroll.
        for (const section of all(".home-access, .home-collections, .home-service")) {
          const targets = section.classList.contains("home-service")
            ? Array.from(section.querySelectorAll<HTMLElement>(".home-service-copy > p, .home-service-copy > h2, .home-service li"))
            : Array.from(section.querySelectorAll<HTMLElement>(".home-section-heading, .home-access-card, .home-collection"));
          entrances.push(gsap.from(targets, {
            y: desktop ? 50 : 24,
            opacity: 0.45,
            duration: 0.85,
            stagger: desktop ? 0.09 : 0.06,
            ease: "power2.out",
            immediateRender: false,
            clearProps: "transform,opacity",
            scrollTrigger: {trigger: section, start: "top 88%", once: true},
          }));
        }

        // A restrained photographic drift on desktop, using native scrolling.
        if (desktop && context.conditions.pointer && hero && image) {
          gsap.fromTo(image, {y: -18}, {y: 18, ease: "none", scrollTrigger: {
            trigger: hero, start: "clamp(top top)", end: "bottom top", scrub: 0.65,
          }});
          const service = one(".home-service");
          const serviceImage = one(".home-service-photo > img");
          if (service && serviceImage) gsap.fromTo(serviceImage, {scale: 1.12, y: -24}, {y: 24, ease: "none", scrollTrigger: {
            trigger: service, start: "top bottom", end: "bottom top", scrub: 0.65,
          }});
          const stage = one(".home-visual-stage");
          if (stage) {
            const tiltX = gsap.quickTo(stage, "rotationY", {duration: 0.8, ease: "power3.out"});
            const tiltY = gsap.quickTo(stage, "rotationX", {duration: 0.8, ease: "power3.out"});
            const move = contextSafe((event: PointerEvent) => {
              const box = stage.getBoundingClientRect();
              tiltX(((event.clientX - box.left) / box.width - 0.5) * 5);
              tiltY(((event.clientY - box.top) / box.height - 0.5) * -4);
            });
            const reset = contextSafe(() => {tiltX(0); tiltY(0);});
            stage.addEventListener("pointermove", move);
            stage.addEventListener("pointerleave", reset);
            listeners.push(() => {stage.removeEventListener("pointermove", move); stage.removeEventListener("pointerleave", reset);});
          }
        }
        const band = one(".home-category-band"), track = one(".home-category-track");
        if (desktop && band && track) gsap.fromTo(track, {x: 70}, {x: -70, ease: "none", scrollTrigger: {trigger: band, start: "top bottom", end: "bottom top", scrub: 0.7}});
        for (const collection of all(".home-collection")) {
          const photo = collection.querySelector("img");
          if (photo) gsap.from(photo, {scale: 1.16, duration: 1.25, ease: "power2.out", immediateRender: false, clearProps: "transform", scrollTrigger: {trigger: collection, start: "top 90%", once: true}});
        }
      } else {
        const image = one(".login-scene");
        const sequence = gsap.timeline({defaults: {ease: "power3.out"}});
        if (image) sequence.fromTo(image, {scale: desktop ? 1.16 : 1.06}, {scale: 1, duration: desktop ? 1.65 : 1}, 0);
        sequence.from(all(".login-product-tile"), {y: 90, rotation: 0, scale: 0.72, opacity: 0, duration: 1, stagger: 0.12, ease: "back.out(1.25)", clearProps: "transform,opacity"}, 0.18);
        sequence.from(all(".login-story > span, .login-story > h1, .login-story > p"), {y: desktop ? 36 : 18, opacity: 0.35, duration: 0.85, stagger: 0.09, clearProps: "transform,opacity"}, 0.1);
        sequence.from(one(".login-form-header"), {y: 10, duration: 0.55, clearProps: "transform"}, 0.08);
        entrances.push(sequence);
      }

      // Keyboard interaction settles every entrance immediately. Form controls
      // and navigation themselves are never hidden or placed in a timeline.
      const finishEntrances = contextSafe(() => entrances.forEach(animation => animation.progress(1)));
      element.addEventListener("focusin", finishEntrances);
      const refresh = contextSafe(() => ScrollTrigger.refresh());
      const images = all("img") as HTMLImageElement[];
      images.forEach(image => image.addEventListener("load", refresh));
      let mounted = true;
      void document.fonts.ready.then(() => {if (mounted) refresh();});
      return () => {
        mounted = false;
        listeners.forEach(remove => remove());
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
