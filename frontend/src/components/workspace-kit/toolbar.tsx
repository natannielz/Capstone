import type { ReactNode } from "react";

/**
 * Controls for the list directly below it (ui-standard §6.1): search (240–360 px) and filters on the left, sort, view toggle
 * and bulk actions on the right, 12 px gaps, wrapping with search first on narrow screens. `resultText` ("6 kemasan",
 * "Menampilkan 1–25 dari 120") is announced politely and never includes the viewer's name.
 */
export function Toolbar({ label, search, filters, sort, view, actions, resultText }: {
  label: string; search?: ReactNode; filters?: ReactNode; sort?: ReactNode; view?: ReactNode; actions?: ReactNode; resultText?: ReactNode;
}) {
  const side = sort || view || actions;
  return <>
    <div className="ui-toolbar" role="group" aria-label={label}>
      <div className="ui-toolbar-main">{search}{filters}</div>
      {side && <div className="ui-toolbar-side">{sort}{view}{actions}</div>}
    </div>
    {resultText !== undefined && <p className="ui-toolbar-result" aria-live="polite">{resultText}</p>}
  </>;
}
