const ORDER_LIMIT = 1_000_000;

/** Next valid package quantity: a multiple of `increment`, at least `minimum`, at most the order limit. Text that is
 * not a whole number starts from the lowest valid quantity. */
export function stepQuantity(text: string, minimum: number, increment: number, direction: 1 | -1): string {
  const lowest = Math.ceil(minimum / increment) * increment, highest = Math.floor(ORDER_LIMIT / increment) * increment;
  if (!/^[0-9]{1,7}$/.test(text.trim())) return String(lowest);
  const current = Number(text.trim());
  const next = direction > 0 ? (Math.floor(current / increment) + 1) * increment : (Math.ceil(current / increment) - 1) * increment;
  return String(Math.min(highest, Math.max(lowest, next)));
}

/** Package quantity with − / + buttons sized for touch; typing stays possible and the server still validates. */
export function QuantityStepper({ id, label, value, minimum, increment, onChange, disabled = false }: {
  id: string; label: string; value: string; minimum: number; increment: number; onChange: (text: string) => void; disabled?: boolean;
}) {
  const name = label.charAt(0).toLowerCase() + label.slice(1);
  const atLowest = stepQuantity(value, minimum, increment, -1) === value.trim() && value.trim() !== "";
  return <div className="quantity-stepper">
    <label htmlFor={id}>{label}</label>
    <div className="quantity-stepper-controls">
      <button type="button" aria-label={"Kurangi " + name} disabled={disabled || atLowest} onClick={() => onChange(stepQuantity(value, minimum, increment, -1))}>−</button>
      <input id={id} required inputMode="numeric" maxLength={7} value={value} disabled={disabled} onChange={event => onChange(event.target.value)} />
      <button type="button" aria-label={"Tambah " + name} disabled={disabled} onClick={() => onChange(stepQuantity(value, minimum, increment, 1))}>+</button>
    </div>
  </div>;
}
