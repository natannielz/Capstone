/**
 * The one place that formats money, numbers, dates and times for display.
 * Rules (EYD V): "Rp1.250.000" — no space after Rp, dot thousands, comma decimals; times "14.30 WIB".
 * Money is grouped with exact decimal/BigInt arithmetic so API decimal strings never pass through floats,
 * and nothing depends on ICU defaults that differ between Node versions and browsers.
 */
const TIME_ZONE = "Asia/Jakarta";
type Decimal = number | string | bigint;

function decimalParts(value: Decimal, fractionDigits: number): { negative: boolean; whole: string; fraction: string } {
  let text: string;
  if (typeof value === "bigint") text = value.toString();
  else if (typeof value === "number") {
    if (!Number.isFinite(value) || Math.abs(value) >= 1e21) throw new RangeError("Nilai bukan angka yang dapat ditampilkan");
    text = Number.isInteger(value) ? BigInt(value).toString() : value.toFixed(Math.min(100, fractionDigits + 6));
  } else text = value.trim();
  const match = /^(-?)(\d+)(?:\.(\d+))?$/.exec(text);
  if (!match) throw new RangeError(`Nilai "${String(value)}" bukan angka`);
  const [, sign, wholeDigits, fractionDigitsText = ""] = match;
  const padded = fractionDigitsText.padEnd(fractionDigits + 1, "0");
  let scaled = BigInt(wholeDigits + padded.slice(0, fractionDigits));
  if (Number(padded[fractionDigits]) >= 5) scaled += BigInt(1); // half away from zero
  const digits = scaled.toString().padStart(fractionDigits + 1, "0");
  const whole = digits.slice(0, digits.length - fractionDigits);
  const fraction = digits.slice(digits.length - fractionDigits);
  return { negative: sign === "-" && scaled !== BigInt(0), whole, fraction };
}

function grouped(whole: string): string {
  return whole.replace(/\B(?=(\d{3})+(?!\d))/g, ".");
}

/** "Rp1.250.000"; decimals: 2 gives "Rp1.250.000,50". Negative values read "-Rp25.000". */
export function formatRupiah(value: Decimal, opts: { decimals?: 0 | 2 } = {}): string {
  const fractionDigits = opts.decimals ?? 0;
  const { negative, whole, fraction } = decimalParts(value, fractionDigits);
  return `${negative ? "-" : ""}Rp${grouped(whole)}${fractionDigits ? "," + fraction : ""}`;
}

/** Server amounts with a currency code: IDR uses formatRupiah; anything else is "CODE 1.500,50". */
export function formatMoney(currency: string, value: Decimal, decimals: 0 | 2 = 2): string {
  return currency === "IDR" ? formatRupiah(value, { decimals }) : `${currency} ${formatNumber(value, decimals)}`;
}

/** "1.234.567"; fractionDigits fixes the comma decimals ("2,5"). */
export function formatNumber(value: Decimal, fractionDigits = 0): string {
  const { negative, whole, fraction } = decimalParts(value, fractionDigits);
  return `${negative ? "-" : ""}${grouped(whole)}${fractionDigits ? "," + fraction : ""}`;
}

/** Ratio 0.873 → "87,3%" with fractionDigits 1. */
export function formatPercent(ratio: number, fractionDigits = 0): string {
  if (!Number.isFinite(ratio)) throw new RangeError("Persentase bukan angka");
  return formatNumber((ratio * 100).toFixed(fractionDigits + 4), fractionDigits) + "%";
}

export type DateInput = Date | string | number;

function toDate(value: DateInput): Date {
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) throw new RangeError(`Tanggal "${String(value)}" tidak valid`);
  return date;
}

function parts(value: DateInput, options: Intl.DateTimeFormatOptions, locale = "id-ID"): Record<string, string> {
  const result: Record<string, string> = {};
  for (const part of new Intl.DateTimeFormat(locale, { timeZone: TIME_ZONE, ...options }).formatToParts(toDate(value)))
    if (part.type !== "literal") result[part.type] = part.value;
  return result;
}

/** "17 Okt 2026"; "long" gives "17 Oktober 2026"; "full" gives "Sabtu, 17 Oktober 2026". Jakarta calendar day. */
export function formatDate(value: DateInput, style: "short" | "long" | "full" = "short"): string {
  if (style === "full") {
    const p = parts(value, { weekday: "long", day: "numeric", month: "long", year: "numeric" });
    return `${p.weekday}, ${p.day} ${p.month} ${p.year}`;
  }
  const p = parts(value, { day: "numeric", month: style === "long" ? "long" : "short", year: "numeric" });
  return `${p.day} ${p.month.replace(/\.$/, "")} ${p.year}`;
}

/** "Oktober 2026". */
export function formatMonth(value: DateInput): string {
  const p = parts(value, { month: "long", year: "numeric" });
  return `${p.month} ${p.year}`;
}

/** Jakarta calendar date as "2026-10-17" (for date inputs, grouping and comparisons, not display). */
export function jakartaDateKey(value: DateInput): string {
  const p = parts(value, { year: "numeric", month: "2-digit", day: "2-digit" }, "en-CA");
  return `${p.year}-${p.month}-${p.day}`;
}

/** "14.30 WIB"; seconds: true gives "14.30.05 WIB". */
export function formatTime(value: DateInput, opts: { seconds?: boolean } = {}): string {
  const p = parts(value, { hour: "2-digit", minute: "2-digit", ...(opts.seconds ? { second: "2-digit" } : {}), hourCycle: "h23" });
  return `${p.hour}.${p.minute}${opts.seconds ? "." + p.second : ""} WIB`;
}

/** "17 Okt 2026 14.30 WIB". */
export function formatDateTime(value: DateInput, opts: { seconds?: boolean } = {}): string {
  return `${formatDate(value)} ${formatTime(value, opts)}`;
}

/** Drop-in replacements for former `new Intl.*Format(...)` instances whose `.format()` call sites remain. */
export const dateTimeFormatter = { format: (value: DateInput) => formatDateTime(value) };
export const dateFormatter = { format: (value: DateInput) => formatDate(value) };
export const longDateFormatter = { format: (value: DateInput) => formatDate(value, "long") };
export const numberFormatter = { format: (value: number | bigint) => formatNumber(value) };
