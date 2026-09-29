import { location } from "./stock-count-fixtures";
export const pool = "9007199254740993";
export const preview = { physicalPoolId: pool, poolVersion: "9007199254740994", suspensionVersion: "0", active: false,
  configurationHash: "a".repeat(64), affectedLocations: [location, { ...location, channelId: "104" }] };
export const outcome = { suspensionId: "9007199254740995", physicalPoolId: pool, poolVersion: preview.poolVersion, suspensionVersion: "1", status: "suspended" };
export const detail = { id: outcome.suspensionId, physicalPoolId: pool, poolVersion: preview.poolVersion, suspensionVersion: "1",
  physicalKey: "warehouse-a", ownerLocation: location, affectedLocations: preview.affectedLocations, wasActive: false,
  suspendedAt: "2026-09-29T00:00:00Z", reason: "Synthetic stop" };
