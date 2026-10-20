/**
 * Render quality for the landing scene. "cinematic" adds ground-truth ambient occlusion, physical depth of field, a
 * subtle bloom, glass bottles and the dust-lit light shaft; "balanced" keeps the same story and shadows without them.
 */
export type QualityTier = "cinematic" | "balanced";
export type QualityEnvironment = { phone: boolean; cores: number; override: QualityTier | null };

export function qualityTier({ phone, cores, override }: QualityEnvironment): QualityTier {
  if (override) return override;
  return !phone && cores >= 6 ? "cinematic" : "balanced";
}

/** `?scene=cinematic|balanced` forces a tier (used to verify both); anything else means automatic. */
export function qualityOverride(search: string): QualityTier | null {
  const value = new URLSearchParams(search).get("scene");
  return value === "cinematic" || value === "balanced" ? value : null;
}

const FRAME_BUDGET_MS = 24, MIN_SAMPLES = 45;

/** Step down when the median of enough measured frames misses the budget (about 40 fps). */
export function shouldDowngrade(frameTimes: readonly number[]): boolean {
  if (frameTimes.length < MIN_SAMPLES) return false;
  const sorted = [...frameTimes].sort((a, b) => a - b);
  return sorted[Math.floor(sorted.length / 2)] > FRAME_BUDGET_MS;
}

/** Bokeh aperture per shot: gentle on the hero, shallowest over the packing close-up, deeper on the road, soft at the counter. */
export function focusForShot(progress: number): { aperture: number; maxBlur: number } {
  const keys = [0.0016, 0.0021, 0.0024, 0.0008, 0.0005], index = Math.max(0, Math.min(keys.length - 2, Math.floor(progress)));
  const t = Math.max(0, Math.min(1, progress - index)), aperture = keys[index] + (keys[index + 1] - keys[index]) * t;
  return { aperture, maxBlur: 0.009 };
}
