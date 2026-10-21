import type { ReactNode } from "react";

/**
 * The one page header (ui-standard §4): optional breadcrumb, the page's only h1 with an optional badge (`meta`), a one-line
 * purpose, and at most two actions — the secondary first, the single primary outermost right. Refresh belongs in
 * `secondaryAction`, never loose in the page body. `tabs` (a <Tabs>) sits under the purpose line.
 */
export function PageHeader({ title, description, primaryAction, secondaryAction, breadcrumb, meta, tabs }: {
  title: ReactNode; description?: ReactNode; primaryAction?: ReactNode; secondaryAction?: ReactNode;
  breadcrumb?: ReactNode; meta?: ReactNode; tabs?: ReactNode;
}) {
  const actions = primaryAction || secondaryAction;
  return <header className="page-header" data-kit="">
    {breadcrumb && <nav aria-label="Lokasi halaman" className="page-header-crumb">{breadcrumb}</nav>}
    <div className="page-header-row">
      <div className="page-header-text">
        <div className="page-header-title"><h1>{title}</h1>{meta}</div>
        {description && <p className="page-header-description">{description}</p>}
      </div>
      {actions && <div className="page-header-actions ui-actions">{secondaryAction}{primaryAction}</div>}
    </div>
    {tabs && <div className="page-header-tabs">{tabs}</div>}
  </header>;
}
