import type { NextConfig } from "next";
const config:NextConfig={allowedDevOrigins:["127.0.0.1"],outputFileTracingIncludes:{"/api/**/*":["./drizzle/*.sql"]},serverExternalPackages:["@libsql/client"],images:{formats:["image/webp","image/avif"]}};
export default config;
