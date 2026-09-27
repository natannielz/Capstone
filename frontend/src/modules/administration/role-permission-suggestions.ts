import type { components } from "../../api/generated/schema";

type Schemas = components["schemas"];

/** Suggestions guide explicit selection only; current server policy decides every request. */
export function rolePermissionSuggestions(catalog: Schemas["AccessCatalogDto"], templateId: string,
  scopeKind: Schemas["GrantScopeKind"] | undefined): Schemas["AccessPermissionDto"][] {
  const role = catalog.roles.find(item => item.id === templateId);
  if (!role || !scopeKind) return [];
  const suggested = "suggestedPermissions" in role ? role.suggestedPermissions : null;
  if (!Array.isArray(suggested)) return [];
  const result: Schemas["AccessPermissionDto"][] = [];
  for (const id of new Set(suggested)) {
    if (typeof id !== "string" || role.permissions.includes(id)) continue;
    const definitions = catalog.permissions.filter(permission => permission.id === id);
    if (definitions.length === 1 && definitions[0].allowedScopes.includes(scopeKind)) result.push(definitions[0]);
  }
  return result;
}
