type ApplicationHistoryState = { unitTokoIndex?: number };

/** Keep application metadata only. Next adds its private router state itself. */
export function applicationHistoryState(state: unknown, unitTokoIndex?: number): ApplicationHistoryState {
  const previous = state && typeof state === "object" && "unitTokoIndex" in state ? state.unitTokoIndex : undefined;
  const index = unitTokoIndex ?? previous;
  return typeof index === "number" && Number.isSafeInteger(index) && index >= 0 ? { unitTokoIndex: index } : {};
}

export function workspaceHistoryIndex(state: unknown): number {
  return applicationHistoryState(state).unitTokoIndex ?? 0;
}
