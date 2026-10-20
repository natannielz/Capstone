import {
  CylinderGeometry, DoubleSide, Group, Mesh, MeshBasicMaterial, MeshDepthMaterial, type Object3D, OrthographicCamera, PlaneGeometry, type Scene,
  WebGLRenderTarget, type WebGLRenderer,
} from "three";
import { FullScreenQuad } from "three/examples/jsm/postprocessing/Pass.js";
import { HorizontalBlurShader } from "three/examples/jsm/shaders/HorizontalBlurShader.js";
import { VerticalBlurShader } from "three/examples/jsm/shaders/VerticalBlurShader.js";
import { ShaderMaterial } from "three";
import type { Kit } from "./kit";

/** A seamless studio: matte floor, a curved backdrop that fades into the fog, and room for the route to the branch. */
export function buildStudio(kit: Kit) {
  const group = new Group();
  const floor = new Mesh(kit.track(new PlaneGeometry(60, 30)), kit.material.floor);
  floor.rotation.x = -Math.PI / 2; floor.position.set(8, 0, -4); floor.receiveShadow = true;
  // A quarter cylinder bends the floor up into the wall, so there is never a horizon line behind the box.
  const sweep = new Mesh(kit.track(new CylinderGeometry(6, 6, 60, 48, 1, true, Math.PI, Math.PI / 2)), kit.material.backdrop);
  sweep.rotation.z = Math.PI / 2; sweep.position.set(8, 6, -13); sweep.material.side = DoubleSide; sweep.receiveShadow = true;
  const wall = new Mesh(kit.track(new PlaneGeometry(60, 30)), kit.material.backdrop);
  wall.position.set(8, 21, -19);
  group.add(floor, sweep, wall);
  return { group, excluded: [floor, sweep, wall] as Object3D[] };
}

/**
 * Soft contact shadows, as product photography has them: the scene is rendered from below into a depth-darkness
 * target, blurred twice and laid on the floor under the subject. Re-rendered only when something near it moves.
 */
export function buildContactShadows(renderer: WebGLRenderer, kit: Kit, options: { size: number; resolution: number; far: number; blur: number; opacity: number }) {
  const { size, resolution, far, blur, opacity } = options;
  const target = new WebGLRenderTarget(resolution, resolution), blurTarget = new WebGLRenderTarget(resolution, resolution);
  target.texture.generateMipmaps = blurTarget.texture.generateMipmaps = false;
  const plane = new Mesh(kit.track(new PlaneGeometry(size, size).rotateX(Math.PI / 2)),
    new MeshBasicMaterial({ map: target.texture, transparent: true, opacity, depthWrite: false }));
  plane.scale.y = -1; plane.renderOrder = 1; plane.position.y = 0.002;
  const camera = new OrthographicCamera(-size / 2, size / 2, size / 2, -size / 2, 0, far); camera.rotation.x = Math.PI / 2;
  const depth = new MeshDepthMaterial({ depthTest: false, depthWrite: false });
  depth.onBeforeCompile = shader => {
    shader.uniforms.darkness = { value: 1.6 };
    shader.fragmentShader = "uniform float darkness;\n" + shader.fragmentShader.replace(
      "gl_FragColor = vec4( vec3( 1.0 - fragCoordZ ), opacity );", "gl_FragColor = vec4( vec3( 0.0 ), ( 1.0 - fragCoordZ ) * darkness );");
  };
  const horizontal = new ShaderMaterial({ ...HorizontalBlurShader, uniforms: { tDiffuse: { value: null }, h: { value: 1 / 256 } }, depthTest: false });
  const vertical = new ShaderMaterial({ ...VerticalBlurShader, uniforms: { tDiffuse: { value: null }, v: { value: 1 / 256 } }, depthTest: false });
  const quad = new FullScreenQuad();
  const pass = (amount: number) => {
    quad.material = horizontal; horizontal.uniforms.tDiffuse.value = target.texture; horizontal.uniforms.h.value = amount / 256;
    renderer.setRenderTarget(blurTarget); quad.render(renderer);
    quad.material = vertical; vertical.uniforms.tDiffuse.value = blurTarget.texture; vertical.uniforms.v.value = amount / 256;
    renderer.setRenderTarget(target); quad.render(renderer);
  };
  return {
    plane,
    /** Centre the shadow catcher under `x, z`, then redraw it, hiding everything listed in `excluded`. */
    render(scene: Scene, excluded: readonly Object3D[], x: number, z: number) {
      plane.position.x = x; plane.position.z = z; camera.position.set(x, 0, z); camera.updateMatrixWorld();
      const visible = excluded.map(object => object.visible); excluded.forEach(object => { object.visible = false; }); plane.visible = false;
      const background = scene.background, fog = scene.fog, previous = renderer.getRenderTarget(), clear = renderer.getClearAlpha();
      scene.background = null; scene.fog = null; scene.overrideMaterial = depth;
      renderer.setRenderTarget(target); renderer.setClearAlpha(0); renderer.clear(); renderer.render(scene, camera);
      scene.overrideMaterial = null;
      pass(blur); pass(blur * 0.4);
      renderer.setRenderTarget(previous); renderer.setClearAlpha(clear);
      scene.background = background; scene.fog = fog; plane.visible = true; excluded.forEach((object, index) => { object.visible = visible[index]; });
    },
    dispose() { target.dispose(); blurTarget.dispose(); depth.dispose(); horizontal.dispose(); vertical.dispose(); quad.dispose(); (plane.material as MeshBasicMaterial).dispose(); },
  };
}
export type ContactShadows = ReturnType<typeof buildContactShadows>;
