import { cloneElement, type ReactElement, type ReactNode } from "react";
import { CircleAlert } from "lucide-react";

type Control = ReactElement<{ id?: string; "aria-describedby"?: string }>;

/**
 * One form field (ui-standard §7): label 14/600 → 8 px → a 44 px control → 4 px → help (13 px muted) or error (13/600 with an
 * icon). The control is the single child; Field gives it `id`, `aria-describedby` (help, then error, after any ids it already
 * had) and `aria-invalid` when there is an error. Required is the default; `optional` adds "(opsional)".
 * `width` caps the field: number 160, date 200, rupiah 240, search 360; text fields fill the 720 px form column.
 */
export function Field({ id, label, help, error, optional = false, width, children }: {
  id: string; label: ReactNode; help?: ReactNode; error?: ReactNode; optional?: boolean;
  width?: "number" | "date" | "rupiah" | "search"; children: Control;
}) {
  const helpId = help ? `${id}-help` : null, errorId = error ? `${id}-error` : null;
  const describedBy = [children.props["aria-describedby"], helpId, errorId].filter(Boolean).join(" ") || undefined;
  return <div className="ui-field" data-kit="" data-width={width}>
    <label htmlFor={id} className="ui-field-label">{label}{optional && <span className="ui-field-optional"> (opsional)</span>}</label>
    {cloneElement(children as ReactElement<Record<string, unknown>>, { "aria-describedby": describedBy, id, ...(error ? { "aria-invalid": true } : {}) })}
    {help && <p id={helpId!} className="ui-field-help">{help}</p>}
    {error && <p id={errorId!} className="ui-field-error"><CircleAlert aria-hidden="true" />{error}</p>}
  </div>;
}
