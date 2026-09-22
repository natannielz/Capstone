import Link from "next/link";

/** A compact T held inside a U, drawn on one grid for small-screen legibility. */
export function BrandMark({className = ""}: {className?: string}) {
  return <svg className={`unit-brand-mark ${className}`} viewBox="0 0 64 64" fill="none" aria-hidden="true" focusable="false">
    <path d="M8 8h48v8H36v24h-8V16H8V8Zm0 18h8v12c0 8 6 14 16 14s16-6 16-14V26h8v12c0 13-10 22-24 22S8 51 8 38V26Z" fill="currentColor"/>
  </svg>;
}

type BrandProps = {href?: string; inverse?: boolean; compact?: boolean; context?: string; className?: string};
export function Brand({href = "/", inverse = false, compact = false, context = "Kebutuhan kerja & harian", className = ""}: BrandProps) {
  return <Link href={href} className={`unit-brand${inverse ? " unit-brand--inverse" : ""}${compact ? " unit-brand--compact" : ""} ${className}`} aria-label={`Unit Toko — ${href === "/" ? "beranda" : context}`}>
    <BrandMark/>
    <span className="unit-brand-copy"><span className="unit-brand-name">Unit Toko</span>{context && <span className="unit-brand-context">{context}</span>}</span>
  </Link>;
}
