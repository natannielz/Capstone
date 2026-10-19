export type SceneMode = "animated" | "still" | "none";
export type SceneEnvironment = { webgl: boolean; reducedMotion: boolean; saveData: boolean };

/** The landing scene is decorative: it animates only when it costs the visitor nothing. Less motion or less data gets
 * the final frame; no WebGL keeps the static poster. */
export function sceneMode(environment: SceneEnvironment): SceneMode {
  if (!environment.webgl) return "none";
  return environment.reducedMotion || environment.saveData ? "still" : "animated";
}

export function readSceneEnvironment(view: Window): SceneEnvironment {
  let webgl = false;
  try {
    const context = view.document.createElement("canvas").getContext("webgl2") ?? view.document.createElement("canvas").getContext("webgl");
    webgl = context !== null;
    context?.getExtension("WEBGL_lose_context")?.loseContext();
  } catch { webgl = false; }
  const connection = (view.navigator as Navigator & { connection?: { saveData?: boolean } }).connection;
  return { webgl, reducedMotion: view.matchMedia("(prefers-reduced-motion: reduce)").matches, saveData: connection?.saveData === true };
}
