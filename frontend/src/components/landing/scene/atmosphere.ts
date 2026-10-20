import {
  AdditiveBlending, BufferAttribute, BufferGeometry, CanvasTexture, CylinderGeometry, Group, Mesh, MeshBasicMaterial, Points,
  PointsMaterial, Quaternion, SRGBColorSpace, Vector3,
} from "three";
import { seeded } from "./textures";

const MOTES = 260, BOX = { x: [-3.4, 3.2], y: [0.25, 4.2], z: [-2.2, 2.6] } as const;

function gradient(width: number, height: number, paint: (context: CanvasRenderingContext2D) => void) {
  const canvas = document.createElement("canvas"); canvas.width = width; canvas.height = height; paint(canvas.getContext("2d")!);
  const texture = new CanvasTexture(canvas); texture.colorSpace = SRGBColorSpace; return texture;
}

/**
 * The dock's light: a soft shaft falling from a high window onto the pallet, with dust drifting through it. Both are
 * additive and fog-free, fade out once the story leaves the dock, and the dust only drifts while frames are running.
 */
export function buildAtmosphere() {
  const group = new Group();
  const shaftTexture = gradient(4, 256, context => {
    const fill = context.createLinearGradient(0, 0, 0, 256);
    fill.addColorStop(0, "rgba(255,240,214,0)"); fill.addColorStop(0.25, "rgba(255,240,214,0.55)"); fill.addColorStop(1, "rgba(255,240,214,0)");
    context.fillStyle = fill; context.fillRect(0, 0, 4, 256);
  });
  const shaftMaterial = new MeshBasicMaterial({ map: shaftTexture, transparent: true, opacity: 0.16, blending: AdditiveBlending, depthWrite: false, fog: false, side: 2 });
  const shaftGeometry = new CylinderGeometry(0.5, 2.3, 9, 40, 1, true);
  const shaft = new Mesh(shaftGeometry, shaftMaterial);
  const top = new Vector3(-4.2, 7.6, -3.2), bottom = new Vector3(0.1, -0.2, 0.3), direction = bottom.clone().sub(top).normalize();
  shaft.position.copy(top).add(bottom).multiplyScalar(0.5); shaft.quaternion.copy(new Quaternion().setFromUnitVectors(new Vector3(0, -1, 0), direction));
  shaft.renderOrder = 2;

  const moteTexture = gradient(64, 64, context => {
    const fill = context.createRadialGradient(32, 32, 0, 32, 32, 32);
    fill.addColorStop(0, "rgba(255,246,228,1)"); fill.addColorStop(0.4, "rgba(255,246,228,0.45)"); fill.addColorStop(1, "rgba(255,246,228,0)");
    context.fillStyle = fill; context.fillRect(0, 0, 64, 64);
  });
  const random = seeded(41), base = new Float32Array(MOTES * 3), positions = new Float32Array(MOTES * 3), phase = new Float32Array(MOTES);
  for (let index = 0; index < MOTES; index++) {
    base[index * 3] = BOX.x[0] + random() * (BOX.x[1] - BOX.x[0]);
    base[index * 3 + 1] = BOX.y[0] + random() * (BOX.y[1] - BOX.y[0]);
    base[index * 3 + 2] = BOX.z[0] + random() * (BOX.z[1] - BOX.z[0]);
    phase[index] = random() * Math.PI * 2;
  }
  positions.set(base);
  const moteGeometry = new BufferGeometry(); moteGeometry.setAttribute("position", new BufferAttribute(positions, 3));
  const moteMaterial = new PointsMaterial({ map: moteTexture, size: 0.045, sizeAttenuation: true, transparent: true, opacity: 0.6, blending: AdditiveBlending, depthWrite: false, fog: false });
  const motes = new Points(moteGeometry, moteMaterial); motes.renderOrder = 3;
  group.add(shaft, motes);

  return {
    group,
    /** `presence` fades the light with the dock (1 in the hero, 0 on the road); `time` drifts the dust in seconds. */
    update(presence: number, time: number | null) {
      group.visible = presence > 0.01;
      shaftMaterial.opacity = 0.16 * presence; moteMaterial.opacity = 0.6 * presence;
      if (time === null || !group.visible) return;
      const span = BOX.y[1] - BOX.y[0];
      for (let index = 0; index < MOTES; index++) {
        const p = phase[index];
        positions[index * 3] = base[index * 3] + Math.sin(time * 0.21 + p) * 0.18;
        positions[index * 3 + 1] = BOX.y[0] + ((base[index * 3 + 1] - BOX.y[0] + time * 0.05 * (0.6 + (p % 1))) % span);
        positions[index * 3 + 2] = base[index * 3 + 2] + Math.cos(time * 0.17 + p) * 0.14;
      }
      moteGeometry.attributes.position.needsUpdate = true;
    },
    dispose() { shaftGeometry.dispose(); shaftMaterial.dispose(); shaftTexture.dispose(); moteGeometry.dispose(); moteMaterial.dispose(); moteTexture.dispose(); },
  };
}
