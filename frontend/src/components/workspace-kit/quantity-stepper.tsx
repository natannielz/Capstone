const ORDER_LIMIT = 1_000_000;

/** Next valid package quantity: a multiple of `increment`, at least `minimum`, at most `maximum` (the order limit by
 * default). Text that is not a whole number starts from the lowest valid quantity. */
export function stepQuantity(text: string, minimum: number, increment: number, direction: 1 | -1, maximum = ORDER_LIMIT): string {
  const lowest = Math.ceil(minimum / increment) * increment, highest = Math.floor(Math.min(maximum, ORDER_LIMIT) / increment) * increment;
  if (!/^[0-9]{1,7}$/.test(text.trim())) return String(lowest);
  const current = Number(text.trim());
  const next = direction > 0 ? (Math.floor(current / increment) + 1) * increment : (Math.ceil(current / increment) - 1) * increment;
  return String(Math.min(highest, Math.max(lowest, next)));
}

/** Package quantity with − / + buttons sized for touch; typing stays possible and the server still validates. */
export function QuantityStepper({ id, label, value, minimum, increment, maximum, onChange, disabled = false }: {
  id: string; label: string; value: string; minimum: number; increment: number; maximum?: number; onChange: (text: string) => void; disabled?: boolean;
}) {
  const name = label.charAt(0).toLowerCase() + label.slice(1), typed = value.trim();
  const atLowest = typed !== "" && stepQuantity(value, minimum, increment, -1, maximum) === typed;
  const atHighest = typed !== "" && stepQuantity(value, minimum, increment, 1, maximum) === typed;
  return <div className="quantity-stepper">
    <label htmlFor={id}>{label}</label>
    <div className="quantity-stepper-controls">
      {/* Not disabled at either end: disabling a focused button drops keyboard focus; at the end it simply changes nothing. */}
      <button type="button" aria-label={"Kurangi " + name} aria-disabled={atLowest || undefined} disabled={disabled} onClick={() => onChange(stepQuantity(value, minimum, increment, -1, maximum))}>−</button>
      <input id={id} required inputMode="numeric" maxLength={7} value={value} disabled={disabled} onChange={event => onChange(event.target.value)} />
      <button type="button" aria-label={"Tambah " + name} aria-disabled={atHighest || undefined} disabled={disabled} onClick={() => onChange(stepQuantity(value, minimum, increment, 1, maximum))}>+</button>
    </div>
  </div>;
}
