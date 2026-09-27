import { isolatedTestEnvironment } from "../../../lib/server/test-environment";

export const dynamic = "force-dynamic";

export async function GET() {
  const environment = isolatedTestEnvironment();
  return environment
    ? Response.json(environment, { headers: { "Cache-Control": "no-store" } })
    : new Response(null, { status: 404 });
}
