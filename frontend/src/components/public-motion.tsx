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
        sequence.from(all(".home13-title-line"), {y: desktop ? 43 : 22, opacity: 0.15, duration: 1.05, stagger: 0.14, ease:"power3.out"}, 0);
        sequence.from(all(".home13-cover > .home13-kicker, .home13-cover-intro > p"), {y: 10, opacity: 0.45, duration: 0.7, stagger: 0.08}, 0.18);
        const heroPhoto = one(".home13-cover-photo");
        const heroImage = one(".home13-cover-photo img");
        const curtain = one(".home13-cover-curtain");
        if (curtain) sequence.fromTo(curtain,{scaleX:1,opacity:1},{scaleX:0,opacity:1,duration:1.25,ease:"power3.inOut",clearProps:"transform,opacity"},0.08);
        if (heroImage) sequence.fromTo(heroImage,{scale:1.15},{scale:desktop?1.075:1,duration:1.55,ease:"power2.out",clearProps:desktop?"opacity":"transform,opacity"},0.08);
        if (desktop && heroPhoto && heroImage) gsap.fromTo(heroImage,{yPercent:-2},{yPercent:2,ease:"none",scrollTrigger:{trigger:heroPhoto,start:"clamp(top bottom)",end:"clamp(bottom top)",scrub:.7}});

        for (const chapter of all(".home13-chapter")) {
          const copy = Array.from(chapter.querySelectorAll<HTMLElement>(".home13-chapter-copy > .home13-kicker, .home13-chapter-copy > h3, .home13-chapter-description, .home13-chapter-detail"));
          entrances.push(gsap.from(copy,{y:desktop?29:16,opacity:.45,stagger:.09,duration:.85,ease:"power3.out",immediateRender:false,clearProps:"transform,opacity",scrollTrigger:{trigger:chapter,start:"top 78%",once:true}}));
          const photo = chapter.querySelector<HTMLElement>(".home13-chapter-photo img");
          const rule = chapter.querySelector<HTMLElement>(".home13-chapter-rule > span");
          const navigation = element.querySelector<HTMLElement>(`.home13-collection-nav a[href="#${chapter.id}"]`);
          if (photo && desktop) gsap.fromTo(photo,{scale:1.085,yPercent:-2.5},{yPercent:2.5,ease:"none",scrollTrigger:{trigger:chapter,start:"clamp(top bottom)",end:"clamp(bottom top)",scrub:.65}});
          else if(photo) entrances.push(gsap.from(photo,{scale:1.055,duration:1.1,ease:"power2.out",immediateRender:false,clearProps:"transform",scrollTrigger:{trigger:photo,start:"top 90%",once:true}}));
          if(rule) gsap.fromTo(rule,{scaleX:0},{scaleX:1,ease:"none",scrollTrigger:{trigger:chapter,start:"clamp(top 70%)",end:"clamp(bottom 35%)",scrub:.35}});
          if(navigation) ScrollTrigger.create({trigger:chapter,start:"top 45%",end:"bottom 45%",toggleClass:{targets:navigation,className:"is-reading"}});
        }
        for (const section of all(".home13-section-intro, .home13-service-intro, .home13-service-steps, .home13-access-grid")) {
          const targets = section.matches(".home13-service-steps") ? Array.from(section.children) : section.matches(".home13-access-grid") ? Array.from(section.querySelectorAll(".home13-access-card > h3, .home13-access-card > p")) : [section];
          entrances.push(gsap.from(targets,{y:desktop?25:12,opacity:.5,duration:.8,stagger:.07,ease:"power2.out",immediateRender:false,clearProps:"transform,opacity",scrollTrigger:{trigger:section,start:"top 88%",once:true}}));
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
      const finishEntrances = () => entrances.forEach(animation => animation.progress(1, true));
      const refresh = () => ScrollTrigger.refresh();
      element.addEventListener("focusin", finishEntrances);
      // Photo frames reserve their height/aspect ratio before images load.
      // Per-image refresh would reset/restore scroll positions during native
      // anchor scrolling, even though no layout measurement has changed.
      let mounted = true;
      void document.fonts.ready.then(() => {if (mounted) refresh();});
      return () => {
        mounted = false;
        element.removeEventListener("focusin", finishEntrances);
      };
    }, element);

    return () => media.revert();
  }, {scope: root, dependencies: [scene, identity], revertOnUpdate: true});
}

export function LandingMotion({children}: {children: ReactNode}) {
  const root = useRef<HTMLDivElement>(null);
  usePublicMotion(root, "landing");
  return <div ref={root} className="public-home public-motion home-v13">{children}</div>;
}
