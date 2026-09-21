import type { NextConfig } from "next";
const config:NextConfig={outputFileTracingIncludes:{"/api/**/*":["./drizzle/*.sql"]},serverExternalPackages:["@libsql/client"],images:{formats:["image/webp","image/avif"]}};
export default config;
