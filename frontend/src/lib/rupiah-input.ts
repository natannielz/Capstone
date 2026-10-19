/**
 * Typed rupiah → the API's canonical decimal ("4250000.00"). Accepts how people actually type or paste amounts:
 * "4.250.000", "4.250.000,50", "Rp 4.250.000,00", "4,250,000", "1000.25".
 * - Both "." and "," present: the last one is the decimal separator, the other groups thousands.
 * - One separator repeated: it groups thousands.
 * - One separator once: followed by exactly three digits it groups thousands ("1.000"); otherwise it is the decimal ("12.50").
 * Thousands groups must be 3 digits; at most 2 decimals and 15 integer digits. Anything else is refused with a reason.
 */
export type RupiahInputResult = { value: string } | { error: string };

const EMPTY = "Isi jumlah dalam rupiah.";
const JUNK = "Gunakan angka saja, misalnya 1.250.000 atau 1250000,50.";
const DECIMALS = "Gunakan paling banyak dua angka di belakang koma.";
const GROUPS = "Pemisah ribuan tidak sesuai. Contoh: 1.250.000.";
const LARGE = "Jumlah terlalu besar.";

function grouped(text: string, separator: string): string | null {
  const groups = text.split(separator);
  // The first group of a grouped number never starts with zero ("0.500" is not five hundred).
  return /^[1-9][0-9]{0,2}$/.test(groups[0]) && groups.slice(1).every(group => /^[0-9]{3}$/.test(group)) ? groups.join("") : null;
}

export function parseRupiahInput(raw: string): RupiahInputResult {
  // "Rp 150.000,-" is the common written price form: the ",-" (or ".-") suffix means no cents.
  const text = raw.trim().replace(/^(rp|idr)\.?/i, "").replace(/\s+/g, "").replace(/[.,]-$/, "");
  if (!text) return { error: EMPTY };
  if (!/^[0-9][0-9.,]*$/.test(text) || /[.,]$/.test(text)) return { error: JUNK };
  const dots = text.split(".").length - 1, commas = text.split(",").length - 1;
  let whole: string | null, fraction = "";
  if (dots && commas) {
    const decimal = text.lastIndexOf(".") > text.lastIndexOf(",") ? "." : ",", group = decimal === "." ? "," : ".";
    if ((decimal === "." ? dots : commas) !== 1) return { error: GROUPS };
    const [integer, rest] = text.split(decimal);
    whole = grouped(integer, group); fraction = rest;
  } else if (dots + commas > 1) {
    whole = grouped(text, dots ? "." : ",");
  } else if (dots + commas === 1) {
    const separator = dots ? "." : ",", [integer, rest] = text.split(separator);
    if (rest.length === 3) whole = grouped(text, separator);
    else { whole = integer; fraction = rest; }
  } else whole = text;
  if (whole === null) return { error: GROUPS };
  if (fraction.length > 2) return { error: DECIMALS };
  const integer = whole.replace(/^0+(?=[0-9])/, "");
  if (integer.length > 15) return { error: LARGE };
  return { value: fraction ? `${integer}.${fraction}` : integer };
}

/** Canonical value or null; `fixed2` pads to exactly two decimals for endpoints that require "1000.50". */
export function rupiahAmount(text: string, fixed2 = false): string | null {
  const parsed = parseRupiahInput(text);
  if ("error" in parsed) return null;
  if (!fixed2) return parsed.value;
  const [whole, fraction = ""] = parsed.value.split(".");
  return `${whole}.${fraction.padEnd(2, "0")}`;
}
