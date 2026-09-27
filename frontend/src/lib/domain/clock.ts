let testNow: (() => Date) | undefined;

export function now(): Date {
  return testNow?.() ?? new Date();
}

export function setTestClock(clock?: () => Date): void {
  if (process.env.NODE_ENV !== "test") throw new Error("Test clock is available only in tests.");
  testNow = clock;
}
