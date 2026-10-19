import { formatRupiah } from "@/lib/format";
import { parseRupiahInput } from "@/lib/rupiah-input";

/**
 * Rupiah amount typed the way people write it ("4.250.000", "Rp 4.250.000,00"). Below the field it states exactly what
 * will be recorded, or why the text cannot be used. The parent submits parseRupiahInput(value).value.
 */
export function RupiahInput({ id, label, value, onChange, required = false, disabled = false, placeholder = "Contoh: 1.250.000" }: {
  id: string; label: string; value: string; onChange: (text: string) => void; required?: boolean; disabled?: boolean; placeholder?: string;
}) {
  const parsed = value.trim() ? parseRupiahInput(value) : null;
  const error = parsed && "error" in parsed ? parsed.error : null, hint = id + "-hint";
  return <div className="rupiah-input">
    <label htmlFor={id}>{label}</label>
    <input id={id} inputMode="decimal" autoComplete="off" required={required} disabled={disabled} maxLength={24} placeholder={placeholder}
      value={value} aria-invalid={error ? true : undefined} aria-describedby={hint} onChange={event => onChange(event.target.value)} />
    {error ? <p id={hint} role="alert" className="rupiah-input-error">{error}</p>
      : <p id={hint} className="rupiah-input-hint">{parsed && "value" in parsed ? `Akan dicatat: ${formatRupiah(parsed.value, { decimals: 2 })}` : "Tulis dalam rupiah, misalnya 1.250.000."}</p>}
  </div>;
}
