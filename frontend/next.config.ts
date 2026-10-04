import path from "node:path";
import { readFileSync } from "node:fs";
import type { NextConfig } from "next";

// The target must be the approved private/trusted API ingress described in identity-hosting.md.
// Local pilot's HTTPS edge routes API traffic directly and does not use this rewrite.
const apiOrigin = process.env.UNIT_TOKO_API_ORIGIN;
if (apiOrigin) {
  const parsed = new URL(apiOrigin);
  if (parsed.protocol !== "https:" || parsed.origin !== apiOrigin || parsed.username || parsed.password)
    throw new Error("UNIT_TOKO_API_ORIGIN must be an HTTPS origin without credentials or path.");
}

const config: NextConfig = {
  // Full-workflow rehearsals must not replace the persistent Release 1 pilot build.
  distDir: process.env.NEXT_PUBLIC_UNIT_TOKO_RELEASE === "full" ? ".next-full" : ".next",
  async rewrites() {
    return { beforeFiles: apiOrigin ? [
      { source: "/api/v1/:path*", destination: `${apiOrigin}/api/v1/:path*` },
      { source: "/auth/:path*", destination: `${apiOrigin}/auth/:path*` },
    ] : [], afterFiles: [], fallback: [] };
  },
  allowedDevOrigins: ["127.0.0.1"],
  outputFileTracingRoot: path.resolve(process.cwd(), ".."),
  outputFileTracingIncludes: {"/api/**/*": ["../backend/legacy/drizzle/*.sql", "./node_modules/@libsql/*/*.node", "./node_modules/@libsql/*/package.json"]},
  serverExternalPackages: ["@libsql/client"],
  webpack(config, { nextRuntime }) {
    if (nextRuntime === "nodejs") {
      // Legacy source is outside the project and has a separate physical install.
      // Next's default external check bundles it when that path differs from the
      // frontend install, which pulls libsql native loaders into webpack.
      const frontendLock = JSON.parse(readFileSync(path.resolve(process.cwd(), "package-lock.json"), "utf8"));
      const legacyLock = JSON.parse(readFileSync(path.resolve(process.cwd(), "../backend/legacy/package-lock.json"), "utf8"));
      for (const name of ["@libsql/client", "libsql"]) {
        const key = `node_modules/${name}`;
        if (frontendLock.packages[key]?.version !== legacyLock.packages[key]?.version) {
          throw new Error(`Legacy and frontend ${name} versions must match for the external runtime import.`);
        }
      }
      const prior = config.externals;
      config.externals = [
        ({ request }: { request?: string }, callback: (error?: Error | null, result?: string) => void) => {
          if (request === "@libsql/client") return callback(null, "commonjs @libsql/client");
          callback();
        },
        ...(Array.isArray(prior) ? prior : prior ? [prior] : []),
      ];
    }
    return config;
  },
  images: { formats: ["image/webp", "image/avif"] },
};
export default config;
