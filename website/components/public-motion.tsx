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
      const one = (selector: string) => element.querySelector<HTMLElement>(selector);
      const all = (selector: string) => Array.from(element.querySelectorAll<HTMLElement>(selector));

      if (scene === "landing") {
        const headline = all(".home-title-line");
        const hero = one(".home-hero");
        const image = one(".home-hero-visual > img");
        const sequence = gsap.timeline({defaults: {ease: "power3.out"}});
        sequence.from(headline, {y: desktop ? 26 : 14, opacity: 0.55, duration: 0.78, stagger: 0.09, clearProps: "transform,opacity"}, 0);
        sequence.from(all(".home-hero-copy > .home-eyebrow, .home-intro, .home-hero-note"), {y: 10, opacity: 0.6, duration: 0.62, stagger: 0.08, clearProps: "transform,opacity"}, 0.12);
        if (image) sequence.fromTo(image, {scale: 1.075}, {scale: desktop ? 1.04 : 1, duration: 1.1}, 0);
        entrances.push(sequence);

        // Brief, single entrances. No content is hidden while waiting to scroll.
        for (const section of all(".home-access, .home-collections, .home-service")) {
          const targets = section.classList.contains("home-service")
            ? Array.from(section.querySelectorAll<HTMLElement>(".home-service-copy > p, .home-service-copy > h2, .home-service li"))
            : Array.from(section.querySelectorAll<HTMLElement>(".home-section-heading, .home-access-card, .home-collection"));
          entrances.push(gsap.from(targets, {
            y: desktop ? 24 : 14,
            opacity: 0.7,
            duration: 0.62,
            stagger: desktop ? 0.09 : 0.06,
            ease: "power2.out",
            immediateRender: false,
            clearProps: "transform,opacity",
            scrollTrigger: {trigger: section, start: "top 88%", once: true},
          }));
        }

        // A restrained photographic drift on desktop, using native scrolling.
        if (desktop && context.conditions.pointer && hero && image) {
          gsap.fromTo(image, {y: -8}, {y: 8, ease: "none", scrollTrigger: {
            trigger: hero, start: "clamp(top top)", end: "bottom top", scrub: 0.65,
          }});
          const service = one(".home-service");
          const serviceImage = one(".home-service-photo > img");
          if (service && serviceImage) gsap.fromTo(serviceImage, {scale: 1.045, y: -10}, {y: 10, ease: "none", scrollTrigger: {
            trigger: service, start: "top bottom", end: "bottom top", scrub: 0.65,
          }});
        }
      } else {
        const image = one(".login-scene");
        const sequence = gsap.timeline({defaults: {ease: "power3.out"}});
        if (image) sequence.fromTo(image, {scale: desktop ? 1.065 : 1.025}, {scale: 1, duration: desktop ? 1.2 : 0.75}, 0);
        sequence.from(all(".login-story > span, .login-story > h1, .login-story > p"), {y: desktop ? 20 : 10, opacity: 0.65, duration: 0.7, stagger: 0.08, clearProps: "transform,opacity"}, 0.06);
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
