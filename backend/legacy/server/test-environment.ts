/** A mutation smoke run must prove that the server uses this run's disposable DB. */
export function isolatedTestEnvironment(environment: Readonly<Record<string, string | undefined>> = process.env) {
  const runId = environment.TOKO_TEST_RUN_ID;
  if (environment.NODE_ENV !== "development" || environment.TOKO_TEST_MODE !== "isolated") return null;
  if (!runId || !/^[a-zA-Z0-9_-]{16,80}$/.test(runId)) return null;
  if (environment.TURSO_DATABASE_URL !== `file:.test-data/${runId}.db`) return null;
  return { mode: "isolated", runId } as const;
}
