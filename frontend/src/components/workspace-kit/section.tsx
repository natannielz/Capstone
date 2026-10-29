import type { ReactNode } from "react";

/** Stable heading id from the title ("Invoice terbit" → "section-invoice-terbit"); pass `id` when two sections share a title. */
export function sectionId(title: string): string {
  return "section-" + title.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
}

/**
 * A page region (ui-standard §5): `<section aria-labelledby>` with an h2, an optional one-line description and up to two
 * secondary/tertiary actions. `card` draws the surface; `plain` puts the title on the canvas for a group that holds its own
 * cards. One level of boxing only: never put a bordered card inside a card Section — use DataList rows or an inset panel.
 * Only the header carries data-kit, so legacy content inside the body keeps the page's element styles.
 */
export function Section({ title, description, actions, children, variant = "card", id, label, busy }: {
  title: string; description?: ReactNode; actions?: ReactNode; children?: ReactNode; variant?: "card" | "plain"; id?: string;
  /** Keeps an existing region name (rehearsals find regions by name) when it differs from the visible h2. */
  label?: string;
  /** aria-busy while the region's content is being read. */
  busy?: boolean;
}) {
  const headingId = id ?? sectionId(title);
  return <section className="ui-section" data-variant={variant} aria-labelledby={label ? undefined : headingId} aria-label={label} aria-busy={busy}>
    <div className="ui-section-header" data-kit="">
      <div><h2 id={headingId}>{title}</h2>{description && <p className="ui-section-description">{description}</p>}</div>
      {actions && <div className="ui-actions">{actions}</div>}
    </div>
    <div className="ui-section-body">{children}</div>
  </section>;
}
