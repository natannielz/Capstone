import { CylinderGeometry, Group, LatheGeometry, Mesh, type Object3D, Sprite, Vector2 } from "three";
import { RoundedBoxGeometry } from "three/examples/jsm/geometries/RoundedBoxGeometry.js";
import { COFFEE_MAXIMUM, type ItemKind, type StoryFrame, storyItems } from "../journey";
import type { Kit } from "./kit";

/**
 * The five catalog packages that make up the request: coffee (up to three dus), tea, an A4 ream, a pen box and a
 * 500 ml sanitizer bottle. Printed cartons get softly rounded edges, as folded board has; the bottle is lathe-turned
 * with a separate wrap label and pump. Each item's origin is its centre, matching the story's item poses.
 */
export function buildItems(kit: Kit) {
  const m = kit.material;
  const carton = (size: readonly [number, number, number], radius: number, materials: typeof m.coffee) => {
    const mesh = new Mesh(kit.track(new RoundedBoxGeometry(size[0], size[1], size[2], 3, radius)), materials);
    mesh.castShadow = true; mesh.receiveShadow = true; return mesh;
  };
  const sizes = Object.fromEntries(storyItems(COFFEE_MAXIMUM).map(spec => [spec.kind, spec.size])) as Record<ItemKind, readonly [number, number, number]>;
  const coffee = Array.from({ length: COFFEE_MAXIMUM }, () => carton(sizes.coffee, 0.012, m.coffee));
  const tea = carton(sizes.tea, 0.01, m.tea), paper = carton(sizes.paper, 0.018, m.paper), pens = carton(sizes.pens, 0.006, m.pens);
  const sanitizer = bottle(kit, sizes.sanitizer[1]);
  const checks = Array.from({ length: COFFEE_MAXIMUM + 4 }, () => { const sprite = new Sprite(m.check); sprite.visible = false; sprite.renderOrder = 10; return sprite; });

  /** The item meshes in story order for `coffee` dus; unused coffee meshes are hidden. */
  function ordered(coffeeCount: number): Object3D[] {
    return storyItems(coffeeCount).map((spec, index, all) => {
      if (spec.kind === "coffee") return coffee[all.slice(0, index).filter(item => item.kind === "coffee").length];
      return { tea, paper, pens, sanitizer }[spec.kind];
    });
  }
  return {
    all: [...coffee, tea, paper, pens, sanitizer] as Object3D[], checks,
    /** Applies the frame's item poses; returns the item meshes in story order for placing the check marks. */
    apply(frame: StoryFrame, coffeeCount: number): Object3D[] {
      const items = ordered(coffeeCount);
      coffee.forEach(mesh => { mesh.visible = items.includes(mesh); });
      items.forEach((item, index) => {
        const pose = frame.items[index]; if (!pose) return;
        item.position.set(...pose.position); item.rotation.set(...pose.rotation); item.scale.setScalar(pose.scale);
      });
      return items;
    },
  };
}

/** Lathe-turned PET bottle (shoulder, neck, collar), its wrap label and a white dispenser pump. */
function bottle(kit: Kit, height: number) {
  const group = new Group(), radius = 0.085, body = height * 0.68;
  const profile = [
    new Vector2(0, -height / 2), new Vector2(radius * 0.92, -height / 2), new Vector2(radius, -height / 2 + 0.02), new Vector2(radius, -height / 2 + body),
    new Vector2(radius * 0.86, -height / 2 + body + 0.04), new Vector2(radius * 0.42, -height / 2 + body + 0.09), new Vector2(radius * 0.36, -height / 2 + body + 0.11),
    new Vector2(radius * 0.36, height / 2 - 0.1), new Vector2(0, height / 2 - 0.1),
  ];
  const shell = new Mesh(kit.track(new LatheGeometry(profile, 48)), kit.material.bottle); shell.castShadow = true;
  const label = new Mesh(kit.track(new CylinderGeometry(radius * 1.006, radius * 1.006, body * 0.62, 48, 1, true)), kit.material.bottleLabel);
  label.position.y = -height / 2 + body * 0.48; label.castShadow = true;
  const collar = new Mesh(kit.track(new CylinderGeometry(radius * 0.46, radius * 0.46, 0.05, 32)), kit.material.pump);
  collar.position.y = height / 2 - 0.08;
  const stem = new Mesh(kit.track(new CylinderGeometry(radius * 0.16, radius * 0.16, 0.06, 16)), kit.material.pump); stem.position.y = height / 2 - 0.03;
  const head = new Mesh(kit.track(new RoundedBoxGeometry(0.12, 0.035, 0.05, 2, 0.012)), kit.material.pump);
  head.position.set(0.025, height / 2 - 0.0, 0);
  for (const part of [collar, stem, head]) { part.castShadow = true; }
  group.add(shell, label, collar, stem, head);
  return group;
}
export type Items = ReturnType<typeof buildItems>;
