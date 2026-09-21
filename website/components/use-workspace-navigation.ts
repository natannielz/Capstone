"use client";

import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import type { Actor } from "@/lib/domain/accounts";
import { canOpenPage, PAGE_ROLES, type WorkspacePage } from "@/lib/domain/navigation";

export type QueryPatch = Record<string, string | null>;
const routeEvent = "workspace:navigate";
const subscribe = (listener: () => void) => {
  window.addEventListener(routeEvent, listener);
  return () => window.removeEventListener(routeEvent, listener);
};
const emit = () => window.dispatchEvent(new Event(routeEvent));

export function useWorkspaceNavigation(actor: Actor | null, closeMobile: () => void) {
  const dirty = useRef(false);
  const [pendingLeave, setPendingLeave] = useState<{ run: () => void } | null>(null);
  const remembered = useRef<Partial<Record<WorkspacePage, string>>>({});
  const scrollPositions = useRef<Record<string, number>>({});
  const current = useRef({ href: "/workspace", search: "", index: 0 });
  const restoring = useRef(false);
  const permittedPop = useRef(false);
  const committedSnapshot = useCallback(() => current.current.search, []);
  const search = useSyncExternalStore(subscribe, committedSnapshot, () => "");
  const query = new URLSearchParams(search);
  const requestedPage = query.get("view") || "dashboard";
  const page = (Object.hasOwn(PAGE_ROLES, requestedPage) ? requestedPage : "dashboard") as WorkspacePage;
  const selected = query.get("id") || undefined;

  const sync = useCallback(() => {
    current.current = { href: window.location.pathname + window.location.search, search: window.location.search, index: Number(window.history.state?.unitTokoIndex || 0) };
    emit();
  }, []);

  useEffect(() => {
    window.history.replaceState({ ...window.history.state, unitTokoIndex: Number(window.history.state?.unitTokoIndex || 0) }, "");
    sync();
    const previousRestoration = window.history.scrollRestoration;
    window.history.scrollRestoration = "manual";
    const onPop = () => {
      if (restoring.current) { restoring.current = false; return; }
      if (dirty.current && !permittedPop.current) {
        const delta = current.current.index - Number(window.history.state?.unitTokoIndex || 0);
        if (delta) {
          restoring.current = true;
          window.history.go(delta);
          setPendingLeave({ run: () => { dirty.current = false; permittedPop.current = true; window.history.go(-delta); } });
          return;
        }
      }
      permittedPop.current = false;
      scrollPositions.current[current.current.search] = window.scrollY;
      sync();
      closeMobile();
      requestAnimationFrame(() => requestAnimationFrame(() => {
        document.getElementById("workspace-title")?.focus({ preventScroll: true });
        window.scrollTo({ top: scrollPositions.current[current.current.search] || 0, behavior: "instant" });
      }));
    };
    const beforeUnload = (event: BeforeUnloadEvent) => {
      if (dirty.current) { event.preventDefault(); event.returnValue = ""; }
    };
    window.addEventListener("popstate", onPop);
    window.addEventListener("beforeunload", beforeUnload);
    return () => {
      window.removeEventListener("popstate", onPop);
      window.removeEventListener("beforeunload", beforeUnload);
      window.history.scrollRestoration = previousRestoration;
    };
  }, [sync, closeMobile]);

  const onDirtyChange = useCallback((value: boolean) => { dirty.current = value; }, []);
  function requestLeave(run: () => void) {
    if (dirty.current) setPendingLeave({ run });
    else run();
  }
  function confirmLeave() {
    const pending = pendingLeave;
    dirty.current = false;
    setPendingLeave(null);
    pending?.run();
  }
  function go(next: WorkspacePage, id?: string, patch?: QueryPatch) {
    if (actor && !canOpenPage(actor.role, next)) return;
    if (next === page && id === selected && !patch) { closeMobile(); return; }
    requestLeave(() => {
      const previous = new URLSearchParams(window.location.search);
      scrollPositions.current[window.location.search] = window.scrollY;
      previous.delete("id"); previous.delete("returnOrder");
      remembered.current[page] = previous.toString();
      const destination = new URLSearchParams(remembered.current[next] || "");
      destination.set("view", next);
      if (id) destination.set("id", id); else destination.delete("id");
      if (page === "orders" && selected && ["deliveries", "billing"].includes(next)) destination.set("returnOrder", selected);
      for (const [key, value] of Object.entries(patch || {})) {
        if (value === null || value === "") destination.delete(key); else destination.set(key, value);
      }
      window.history.pushState({ ...window.history.state, unitTokoIndex: current.current.index + 1 }, "", `/workspace?${destination}`);
      sync(); closeMobile();
      requestAnimationFrame(() => requestAnimationFrame(() => {
        document.getElementById("workspace-title")?.focus({ preventScroll: true });
        window.scrollTo({ top: !id ? scrollPositions.current[`?${destination}`] || 0 : 0, behavior: "instant" });
      }));
    });
  }
  function updateQuery(patch: QueryPatch) {
    const next = new URLSearchParams(window.location.search);
    for (const [key, value] of Object.entries(patch)) {
      if (value === null || value === "") next.delete(key); else next.set(key, value);
    }
    window.history.replaceState(window.history.state, "", `/workspace?${next}`);
    sync();
  }
  return { page, selected, query, go, updateQuery, onDirtyChange, requestLeave, pendingLeave, confirmLeave, cancelLeave: () => setPendingLeave(null) };
}
