/** WCAG 2.x contrast for design tokens; accepts #rgb, #rrggbb and oklch(L C H) (L as 0–1 or %). */
type Rgb = [number, number, number];

function hexToRgb(value: string): Rgb {
  let hex = value.slice(1);
  if (hex.length === 3) hex = [...hex].map(char => char + char).join("");
  if (!/^[0-9a-f]{6}$/i.test(hex)) throw new Error(`Unsupported hex colour ${value}`);
  return [0, 2, 4].map(index => parseInt(hex.slice(index, index + 2), 16) / 255) as Rgb;
}

function oklchToRgb(value: string): Rgb {
  const parts = value.slice(value.indexOf("(") + 1, value.lastIndexOf(")")).split("/")[0].trim().split(/\s+/);
  if (parts.length < 3) throw new Error(`Unsupported oklch colour ${value}`);
  const lightness = parts[0].endsWith("%") ? parseFloat(parts[0]) / 100 : parseFloat(parts[0]);
  const chroma = parseFloat(parts[1]);
  const hue = (parseFloat(parts[2]) * Math.PI) / 180;
  const a = chroma * Math.cos(hue), b = chroma * Math.sin(hue);
  const l = (lightness + 0.3963377774 * a + 0.2158037573 * b) ** 3;
  const m = (lightness - 0.1055613458 * a - 0.0638541728 * b) ** 3;
  const s = (lightness - 0.0894841775 * a - 1.291485548 * b) ** 3;
  const linear: Rgb = [
    4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
    -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
    -0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s,
  ];
  // Back to gamma-encoded sRGB, clamped to the displayable gamut.
  return linear.map(channel => {
    const clamped = Math.min(1, Math.max(0, channel));
    return clamped <= 0.0031308 ? 12.92 * clamped : 1.055 * clamped ** (1 / 2.4) - 0.055;
  }) as Rgb;
}

function toRgb(value: string): Rgb {
  const trimmed = value.trim().toLowerCase();
  if (trimmed.startsWith("#")) return hexToRgb(trimmed);
  if (trimmed.startsWith("oklch(")) return oklchToRgb(trimmed);
  throw new Error(`Unsupported colour ${value}`);
}

function luminance([r, g, b]: Rgb): number {
  const linear = (channel: number) => (channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4);
  return 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b);
}

export function contrastRatio(foreground: string, background: string): number {
  const [lighter, darker] = [luminance(toRgb(foreground)), luminance(toRgb(background))].sort((x, y) => y - x);
  return (lighter + 0.05) / (darker + 0.05);
}

/** Reads `--name:value` declarations from the first `:root{...}` block. */
export function readTokens(css: string): Record<string, string> {
  const start = css.indexOf(":root");
  if (start < 0) return {};
  const body = css.slice(css.indexOf("{", start) + 1, css.indexOf("}", start));
  const tokens: Record<string, string> = {};
  for (const declaration of body.split(";")) {
    const match = declaration.match(/^\s*--([\w-]+)\s*:\s*(.+?)\s*$/);
    if (match) tokens[match[1]] = match[2];
  }
  return tokens;
}
