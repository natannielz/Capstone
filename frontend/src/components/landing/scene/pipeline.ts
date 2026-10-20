import { HalfFloatType, Vector2, WebGLRenderTarget, type PerspectiveCamera, type Scene, type WebGLRenderer } from "three";
import { EffectComposer } from "three/examples/jsm/postprocessing/EffectComposer.js";
import { RenderPass } from "three/examples/jsm/postprocessing/RenderPass.js";
import { GTAOPass } from "three/examples/jsm/postprocessing/GTAOPass.js";
import { BokehPass } from "three/examples/jsm/postprocessing/BokehPass.js";
import { UnrealBloomPass } from "three/examples/jsm/postprocessing/UnrealBloomPass.js";
import { OutputPass } from "three/examples/jsm/postprocessing/OutputPass.js";
import type { QualityTier } from "./quality";

export type Pipeline = {
  tier: QualityTier;
  render(): void;
  setSize(width: number, height: number): void;
  setFocus(distance: number, aperture: number, maxBlur: number): void;
  dispose(): void;
};

/**
 * The cinematic pipeline renders through a multisampled half-float target: ground-truth ambient occlusion grounds
 * every contact, a physical bokeh puts the label close-ups in shallow focus, a high-threshold bloom lets only bright
 * glints glow, and the output pass applies tone mapping and sRGB. The balanced pipeline renders directly.
 */
/** `overlay` holds billboards (the check mark) drawn after post-processing, so occlusion and blur never touch them. */
export function createPipeline(renderer: WebGLRenderer, scene: Scene, overlay: Scene, camera: PerspectiveCamera, tier: QualityTier): Pipeline {
  const drawOverlay = () => { renderer.autoClear = false; renderer.render(overlay, camera); renderer.autoClear = true; };
  if (tier === "balanced") {
    return { tier, render: () => { renderer.render(scene, camera); drawOverlay(); }, setSize: () => undefined, setFocus: () => undefined, dispose: () => undefined };
  }
  const size = renderer.getDrawingBufferSize(new Vector2());
  const target = new WebGLRenderTarget(size.x, size.y, { type: HalfFloatType, samples: 4 });
  const composer = new EffectComposer(renderer, target);
  const ambient = new GTAOPass(scene, camera, size.x, size.y);
  ambient.updateGtaoMaterial({ radius: 0.45, distanceExponent: 1.2, thickness: 1.4, scale: 1.1, samples: 16 });
  ambient.updatePdMaterial({ lumaPhi: 10, depthPhi: 2, normalPhi: 3, radius: 6, rings: 2, samples: 16 });
  ambient.blendIntensity = 0.85;
  const bokeh = new BokehPass(scene, camera, { focus: 8, aperture: 0.001, maxblur: 0.009 });
  const lens = bokeh.uniforms as Record<"focus" | "aperture" | "maxblur", { value: number }>;
  // Only true highlights (glints on glass caps and clear-coat) exceed the HDR threshold; lit paper and kraft never bloom.
  const bloom = new UnrealBloomPass(new Vector2(size.x, size.y), 0.22, 0.3, 1.9);
  composer.addPass(new RenderPass(scene, camera));
  composer.addPass(ambient); composer.addPass(bokeh); composer.addPass(bloom); composer.addPass(new OutputPass());
  return {
    tier,
    render: () => { composer.render(); drawOverlay(); },
    setSize(width, height) { composer.setPixelRatio(renderer.getPixelRatio()); composer.setSize(width, height); },
    setFocus(distance, aperture, maxBlur) {
      lens.focus.value = distance; lens.aperture.value = aperture; lens.maxblur.value = maxBlur;
    },
    dispose() { composer.passes.forEach(pass => pass.dispose()); composer.dispose(); target.dispose(); },
  };
}
