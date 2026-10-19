'use client';

import { useState } from "react";
import { formatRupiah } from "@/lib/format";
import { parseRupiahInput } from "@/lib/rupiah-input";

/** What the field says: a valid amount always shows what will be recorded; a reason appears only once the field is left. */
export function rupiahInputState(value: string, touched: boolean): { error: string | null; hint: string } {
  if (!value.trim()) return { error: null, hint: "Tulis dalam rupiah, misalnya 1.250.000." };
  const parsed = parseRupiahInput(value);
  if ("value" in parsed) return { error: null, hint: `Akan dicatat: ${formatRupiah(parsed.value, { decimals: 2 })}` };
  return { error: touched ? parsed.error : null, hint: "Tulis dalam rupiah, misalnya 1.250.000." };
}

/**
 * Rupiah amount typed the way people write it ("4.250.000", "Rp 4.250.000,00"). Below the field it states exactly what
 * will be recorded; an unusable amount is explained after the person leaves the field, not on every keystroke.
 * The parent submits parseRupiahInput(value).value.
 */
export function RupiahInput({ id, label, value, onChange, required = false, disabled = false, placeholder = "Contoh: 1.250.000" }: {
  id: string; label: string; value: string; onChange: (text: string) => void; required?: boolean; disabled?: boolean; placeholder?: string;
}) {
  const [touched, setTouched] = useState(false);
  const { error, hint } = rupiahInputState(value, touched), describedBy = id + "-hint";
  return <div className="rupiah-input">
    <label htmlFor={id}>{label}</label>
    <input id={id} inputMode="decimal" autoComplete="off" required={required} disabled={disabled} maxLength={32} placeholder={placeholder}
      value={value} aria-invalid={error ? true : undefined} aria-describedby={describedBy}
      onChange={event => onChange(event.target.value)} onBlur={() => setTouched(true)} />
    {error ? <p id={describedBy} role="alert" className="rupiah-input-error">{error}</p>
      : <p id={describedBy} className="rupiah-input-hint">{hint}</p>}
  </div>;
}
