import Link from "next/link";

/** Unit Toko's joined U/T mark: a shared container and a clear top edge. */
export function BrandMark({className = ""}: {className?: string}) {
  return <svg className={`unit-brand-mark ${className}`} viewBox="0 0 64 64" fill="none" aria-hidden="true" focusable="false">
    <rect width="64" height="64" rx="14" fill="#073B45"/>
    <path d="M15 19v18a10 10 0 0 0 20 0V19" stroke="#FFFFFF" strokeWidth="7" strokeLinecap="square"/>
    <path d="M33 19h19M43 19v28" stroke="#F47B49" strokeWidth="7"/>
  </svg>;
}

type BrandProps = {href?: string; inverse?: boolean; compact?: boolean; context?: string; className?: string};
export function Brand({href = "/", inverse = false, compact = false, context = "Kebutuhan kerja & harian", className = ""}: BrandProps) {
  return <Link href={href} className={`unit-brand${inverse ? " unit-brand--inverse" : ""}${compact ? " unit-brand--compact" : ""} ${className}`} aria-label={`Unit Toko — ${href === "/" ? "beranda" : context}`}>
    <BrandMark/>
    <span className="unit-brand-copy"><span className="unit-brand-name">Unit Toko<span aria-hidden="true">.</span></span>{context && <span className="unit-brand-context">{context}</span>}</span>
  </Link>;
}
