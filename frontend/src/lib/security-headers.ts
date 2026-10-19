/**
 * Site-wide response headers (ASVS 5.0 L1, web frontend). CSP without nonces, as in the Next.js guide: scripts and
 * styles from this origin (Next's inline bootstrap needs 'unsafe-inline'), images also from blob:/data: for private
 * evidence previews, and API calls only to this origin (the API is proxied same-origin). Framing is refused everywhere.
 * Development adds 'unsafe-eval' (React's debug stacks) and the optional design-capture script; HSTS is production only.
 */
export function securityHeaders({ development, identityOrigin }: { development: boolean; identityOrigin?: string }): { key: string; value: string }[] {
  // Sign-in/step-up/sign-out forms post here and are redirected to the identity provider; Chrome applies form-action to that redirect.
  if (identityOrigin !== undefined) { const parsed = new URL(identityOrigin); if (parsed.protocol !== "https:" || parsed.origin !== identityOrigin) throw new Error("UNIT_TOKO_IDENTITY_ORIGIN must be an HTTPS origin without path."); }
  const script = ["'self'", "'unsafe-inline'", ...(development ? ["'unsafe-eval'", "https://mcp.figma.com"] : [])].join(" ");
  const csp = [
    "default-src 'self'",
    `script-src ${script}`,
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' blob: data:",
    "font-src 'self'",
    "connect-src 'self'",
    "media-src 'self' blob:",
    "worker-src 'self' blob:",
    "object-src 'none'",
    "base-uri 'self'",
    `form-action 'self' ${identityOrigin ?? "https:"}`,
    "frame-ancestors 'none'",
  ].join("; ");
  return [
    { key: "Content-Security-Policy", value: csp },
    { key: "X-Content-Type-Options", value: "nosniff" },
    { key: "X-Frame-Options", value: "DENY" },
    { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
    { key: "Permissions-Policy", value: "camera=(self), microphone=(), geolocation=(), payment=(), usb=()" },
    ...(development ? [] : [{ key: "Strict-Transport-Security", value: "max-age=63072000; includeSubDomains" }]),
  ];
}
