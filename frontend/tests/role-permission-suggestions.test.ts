import { test } from "node:test";
import assert from "node:assert/strict";
import type { components } from "../src/api/generated/schema";
import { rolePermissionSuggestions } from "../src/modules/administration/role-permission-suggestions";
import { permissionLabel } from "../src/modules/administration/permission-labels";

type Schemas=components["schemas"];
const catalog={roles:[{id:"buyer",label:"Pembeli",version:1,permissions:["order.read"],
 suggestedPermissions:["substitution.accept","stock.convert","unknown.permission","order.read","substitution.accept"]}],
 allowedActions:[],financePacks:[],permissions:[
 {id:"order.read",label:"Baca pesanan",allowedScopes:["buyerOwned"] as Schemas["GrantScopeKind"][],requiresFreshMfa:false},
 {id:"substitution.accept",label:"Persetujuan",allowedScopes:["buyerOwned"] as Schemas["GrantScopeKind"][],requiresFreshMfa:false},
 {id:"stock.convert",label:"Konversi",allowedScopes:["fulfillmentShop"] as Schemas["GrantScopeKind"][],requiresFreshMfa:false},
]};

test("ACC02 role suggestions are known scope-valid extras without duplicate or base permission controls",()=>{
 assert.deepEqual(rolePermissionSuggestions(catalog,"buyer","buyerOwned").map(permission=>permission.id),["substitution.accept"]);
 assert.deepEqual(catalog.roles[0].permissions,["order.read"]);
});

test("ACC02 unknown role, absent scope and old catalogs without suggestions provide no suggestions",()=>{
 assert.deepEqual(rolePermissionSuggestions(catalog,"missing","buyerOwned"),[]);
 assert.deepEqual(rolePermissionSuggestions(catalog,"buyer",undefined),[]);
 for(const suggestedPermissions of [undefined,null]){
  const old={...catalog,roles:[{...catalog.roles[0],suggestedPermissions}]};
  assert.deepEqual(rolePermissionSuggestions(old,"buyer","buyerOwned"),[]);
 }
});

test("ACC02 suggestions are recomputed for a different role or scope without carrying old entries",()=>{
 assert.deepEqual(rolePermissionSuggestions(catalog,"buyer","buyerOwned").map(permission=>permission.id),["substitution.accept"]);
 assert.deepEqual(rolePermissionSuggestions(catalog,"buyer","buyingBranch"),[]);
 assert.deepEqual(rolePermissionSuggestions(catalog,"finance","buyerOwned"),[]);
});

test("ACC02 new business permissions have human-readable Indonesian labels while legacy labels remain",()=>{
 for(const id of ["order.spend.approve","order.accept","substitution.propose","substitution.accept","stock.convert",
  "reservation.release","supplier.change.propose","supplier.change.approve","history.read","report.read"])
  assert.notEqual(permissionLabel(id),id);
 assert.equal(permissionLabel("order.approve"),"Setujui pesanan");
 assert.equal(permissionLabel("order.substitute"),"Tanggapi penggantian barang");
});
