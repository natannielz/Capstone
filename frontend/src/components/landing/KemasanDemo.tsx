'use client';

import { useState } from "react";
import { DEMO_MAXIMUM, DEMO_MINIMUM, KemasanLabel } from "./KemasanLabel";
import { KemasanStage } from "./KemasanStage";

/** Typed text that is not a whole number in range keeps the stage on the nearest valid count. */
function cartonCount(text: string): number {
  const typed = /^[0-9]{1,2}$/.test(text.trim()) ? Number(text.trim()) : DEMO_MINIMUM;
  return Math.min(DEMO_MAXIMUM, Math.max(DEMO_MINIMUM, typed));
}

export function KemasanDemo() {
  const [value, setValue] = useState(String(DEMO_MINIMUM));
  const cartons = cartonCount(value);
  return <>
    <KemasanStage cartons={cartons} />
    <KemasanLabel value={value} cartons={cartons} onChange={setValue} />
  </>;
}
