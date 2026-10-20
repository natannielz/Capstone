import { HalfFloatType, Vector2, WebGLRenderTarget, type PerspectiveCamera, type Scene, type WebGLRenderer } from "three";
import { EffectComposer } from "three/examples/jsm/postprocessing/EffectComposer.js";
import { RenderPass } from "three/examples/jsm/postprocessing/RenderPass.js";
import { GTAOPass } from "three/examples/jsm/postprocessing/GTAOPass.js";
import { BokehPass } from "three/examples/jsm/postprocessing/BokehPass.js";
import { UnrealBloomPass } from "three/examples/jsm/postprocessing/UnrealBloomPass.js";
import { OutputPass } from "three/examples/jsm/postprocessing/OutputPass.js";
import { ShaderPass } from "three/examples/jsm/postprocessing/ShaderPass.js";
import type { QualityTier } from "./quality";

export type Pipeline = {
  tier: QualityTier;
  render(): void;
  setSize(width: number, height: number): void;
  setFocus(distance: number, aperture: number, maxBlur: number): void;
  /** Seconds, for the film grain; a fixed time gives a fixed frame. */
  setTime(seconds: number): void;
  dispose(): void;
};

/**
 * The cinematic pipeline renders through a multisampled half-float target: ground-truth ambient occlusion grounds
 * every contact, a physical bokeh puts the label close-ups in shallow focus, a high-threshold bloom lets only bright
 * glints glow, and the output pass applies tone mapping and sRGB. The balanced pipeline renders directly.
 */
/** `overlay` holds billboards (the check mark) drawn after post-processing, so occlusion and blur never touch them. */
/**
 * The film look, applied after tone mapping: shadows lean a touch teal and highlights a touch warm (the brand's two
 * colours), a soft vignette draws the eye to the subject, and a fine moving grain removes the flat digital finish.
 */
const GradeShader = {
  uniforms: { tDiffuse: { value: null }, time: { value: 0 } },
  vertexShader: "varying vec2 vUv; void main() { vUv = uv; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }",
  fragmentShader: `
    uniform sampler2D tDiffuse; uniform float time; varying vec2 vUv;
    float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }
    void main() {
      vec4 color = texture2D(tDiffuse, vUv);
      float luma = dot(color.rgb, vec3(0.2126, 0.7152, 0.0722));
      color.rgb += mix(vec3(-0.010, 0.004, 0.012), vec3(0.018, 0.007, -0.014), smoothstep(0.2, 0.85, luma));
      vec2 offset = (vUv - 0.5) * vec2(1.0, 0.82);
      color.rgb *= mix(1.0, smoothstep(0.86, 0.22, length(offset)), 0.18);
      color.rgb += (hash(floor(vUv * vec2(1600.0, 900.0)) + fract(time) * 61.0) - 0.5) * 0.028;
      gl_FragColor = color;
    }`,
};

export function createPipeline(renderer: WebGLRenderer, scene: Scene, overlay: Scene, camera: PerspectiveCamera, tier: QualityTier): Pipeline {
  const drawOverlay = () => { renderer.autoClear = false; renderer.render(overlay, camera); renderer.autoClear = true; };
  if (tier === "balanced") {
    return { tier, render: () => { renderer.render(scene, camera); drawOverlay(); }, setSize: () => undefined, setFocus: () => undefined, setTime: () => undefined, dispose: () => undefined };
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
  const grade = new ShaderPass(GradeShader);
  composer.addPass(ambient); composer.addPass(bokeh); composer.addPass(bloom); composer.addPass(new OutputPass()); composer.addPass(grade);
  return {
    tier,
    render: () => { composer.render(); drawOverlay(); },
    setSize(width, height) { composer.setPixelRatio(renderer.getPixelRatio()); composer.setSize(width, height); },
    setFocus(distance, aperture, maxBlur) {
      lens.focus.value = distance; lens.aperture.value = aperture; lens.maxblur.value = maxBlur;
    },
    setTime(seconds) { grade.uniforms.time.value = seconds % 97; },
    dispose() { composer.passes.forEach(pass => pass.dispose()); composer.dispose(); target.dispose(); },
  };
}
