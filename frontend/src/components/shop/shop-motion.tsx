"use client";

import { useRef, type ReactNode, type RefObject } from "react";
import { useGSAP } from "@gsap/react";
import gsap from "gsap";

gsap.registerPlugin(useGSAP);
const motionQuery = "(prefers-reduced-motion: no-preference)";

/** Reveal content only: inputs, purchase buttons, and native scrolling stay stable. */
export function ShopMotion({ children, refreshKey, className = "" }: {
  children: ReactNode;
  refreshKey: string;
  className?: string;
}) {
  const scope = useRef<HTMLDivElement>(null);
  useGSAP(() => {
    const media = gsap.matchMedia();
    media.add(motionQuery, () => {
      const root = scope.current;
      if (!root) return;
      const headline = root.querySelectorAll("[data-shop-headline]");
      const photos = root.querySelectorAll(".shop-product-photo img");
      const timeline = gsap.timeline({ defaults: { ease: "power3.out" } });
      if (headline.length) timeline.from(headline, { y: 16, opacity: 0.3, duration: 0.55, clearProps: "transform,opacity" }, 0);
      if (photos.length) timeline.from(photos, { opacity: 0.45, duration: 0.28, stagger: { each: 0.01, amount: 0.1 }, clearProps: "opacity" }, 0);
      const settle = () => { timeline.progress(1); };
      root.addEventListener("focusin", settle);
      return () => root.removeEventListener("focusin", settle);
    }, scope);
    return () => media.revert();
  }, { scope, dependencies: [refreshKey], revertOnUpdate: true });
  return <div ref={scope} className={className}>{children}</div>;
}

export function useShopShellMotion(scope: RefObject<HTMLDivElement | null>, pathname: string, quantity: number, ready: boolean, pending: boolean) {
  const previous = useRef<number | null>(null);
  useGSAP(() => {
    const root = scope.current;
    if (!root) return;
    const media = gsap.matchMedia();
    media.add(motionQuery, mediaContext => {
      const seen = new WeakSet<Element>();
      // Bind async mutations to this media context. Entering the parent React
      // context synchronously here would create a circular cleanup graph.
      const trackedReveal = mediaContext.add("reveal", () => {
        const nodes = Array.from(root.querySelectorAll(".shop-page-heading h1, .shop-detail-copy h1, .shop-detail-image > img, .customer-page-heading h1, .customer-identity img, .customer-section > h2")).filter(node => !seen.has(node));
        nodes.forEach(node => seen.add(node));
        if (nodes.length) gsap.from(nodes, { y: 12, opacity: 0.5, duration: 0.48, stagger: 0.035, ease: "power2.out", clearProps: "transform,opacity" });
      });
      const reveal = () => { trackedReveal(); };
      reveal();
      const observer = new MutationObserver(reveal);
      observer.observe(root, { childList: true, subtree: true });
      return () => observer.disconnect();
    }, scope);
    return () => media.revert();
  }, { scope, dependencies: [pathname], revertOnUpdate: true });
  useGSAP(() => {
    if (!ready) { previous.current = null; return; }
    const increased = previous.current !== null && quantity > previous.current;
    previous.current = quantity;
    if (!increased || pending) return;
    const media = gsap.matchMedia();
    media.add(motionQuery, () => {
      const badges = scope.current?.querySelectorAll("[data-cart-feedback]");
      if (badges?.length) gsap.fromTo(badges, { scale: 1.16 }, { scale: 1, duration: 0.35, ease: "power2.out", clearProps: "transform" });
    }, scope);
    return () => media.revert();
  }, { scope, dependencies: [quantity, ready, pending], revertOnUpdate: true });
}
