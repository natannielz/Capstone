import { test } from "node:test";
import assert from "node:assert/strict";
import type { components } from "../src/api/generated/schema";
import { invitationScopeOptions, invitationRole, invitationDates } from "../src/modules/identity/invitation-scope";

type Schemas = components["schemas"];
const node=(id:string,kind:Schemas["ScopeNodeKind"],parentId:string|null=null,status="active"):Schemas["AccessNodeDto"]=>({id,kind,parentId,status,version:"1"});
const current: Schemas["CurrentAccessDto"]={userId:"9007199254740993",grantVersion:"1",status:"active",allowedActions:[],grants:[],memberships:[],scopes:[
 node("9007199254740994","buyingOrganization"),node("10","branch","9007199254740994"),node("11","division","10"),
 node("20","branch","9007199254740994","suspended"),node("21","division","20"),
 node("30","fulfillmentOrganization"),node("31","shop","30"),node("32","warehouse","31"),node("33","channel","30"),
 node("40","fulfillmentOrganization"),node("41","shop","40"),node("42","channel","40"),node("43","warehouse","41","suspended")
]};
const catalog: Schemas["AccessCatalogDto"]={allowedActions:[],roles:[{id:"buyer",label:"Pembeli",version:1,permissions:["order.read","branch.pic"]},{id:"finance",label:"Keuangan",version:1,permissions:[]}],financePacks:[{id:"receipts",label:"Penerimaan",permissions:["payment.read"]},{id:"close",label:"Tutup buku",permissions:["period.close"]}],permissions:[{id:"order.read",label:"Baca pesanan",allowedScopes:["buyingDivision"],requiresFreshMfa:false},{id:"branch.pic",label:"PIC cabang",allowedScopes:["buyingDivision","buyingBranch"],requiresFreshMfa:false},{id:"payment.read",label:"Baca dana",allowedScopes:["buyingDivision"],requiresFreshMfa:false},{id:"period.close",label:"Tutup buku",allowedScopes:["system"],requiresFreshMfa:true}]};

test("ACC01 offers only coherent active hierarchy paths and keeps Int64 identifiers as strings",()=>{
 const scopes=invitationScopeOptions(current);
 assert.ok(scopes.some(item=>item.scope.kind==="buyingDivision"&&item.scope.organizationId==="9007199254740994"&&item.scope.branchId==="10"&&item.scope.divisionId==="11"));
 assert.ok(scopes.some(item=>item.scope.kind==="fulfillmentWarehouse"&&item.scope.organizationId==="30"&&item.scope.shopId==="31"&&item.scope.channelId==="33"&&item.scope.warehouseId==="32"));
 assert.equal(scopes.some(item=>item.scope.branchId==="20"||item.scope.divisionId==="21"||item.scope.warehouseId==="43"),false);
 assert.equal(scopes.some(item=>item.scope.organizationId==="30"&&item.scope.shopId==="41"),false);
 assert.equal(scopes.some(item=>item.scope.organizationId==="30"&&item.scope.channelId==="42"),false);
 assert.equal(scopes.some(item=>item.scope.branchId&&item.scope.shopId),false);
});

test("ACC01 role materialization requires one compatible template and only explicitly selected finance packs",()=>{
 const division=invitationScopeOptions(current).find(item=>item.scope.kind==="buyingDivision")!.scope;
 assert.deepEqual(invitationRole(catalog,division,"buyer",[]),{templateId:"buyer",financePacks:[],additionalPermissions:[],delegablePermissions:[]});
 assert.equal(invitationRole(catalog,division,"buyer",["receipts"]),null);
 assert.deepEqual(invitationRole(catalog,{kind:"system"},"finance",["close"]),{templateId:"finance",financePacks:["close"],additionalPermissions:[],delegablePermissions:[]});
 assert.equal(invitationRole(catalog,division,"buyer",["close"]),null);
 assert.equal(invitationRole(catalog,division,"missing",[]),null);
 assert.equal(invitationRole(catalog,division,"buyer",["receipts","receipts"]),null);
 const branch=invitationScopeOptions(current).find(item=>item.scope.kind==="buyingBranch")!.scope;
 const broad={...catalog,roles:catalog.roles.map(role=>role.id==="buyer"?{...role,permissions:["order.read"]}:role),permissions:catalog.permissions.map(permission=>permission.id==="order.read"?{...permission,allowedScopes:[...permission.allowedScopes,"buyingBranch" as const]}:permission)};
 assert.equal(invitationRole(broad,branch,"buyer",[]),null,"broad buyer access needs an explicit branch designation");
});

test("ACC01 non-finance store template cannot receive a compatible reporting pack",()=>{
 const shop=invitationScopeOptions(current).find(item=>item.scope.kind==="fulfillmentShop")!.scope;
 const fixture:Schemas["AccessCatalogDto"]={allowedActions:[],roles:[{id:"store-staff",label:"Staf toko",version:1,permissions:["stock.read"]}],financePacks:[{id:"reporting",label:"Laporan",permissions:["report.read"]}],permissions:[
  {id:"stock.read",label:"Baca stok",allowedScopes:["fulfillmentShop"],requiresFreshMfa:false},
  {id:"report.read",label:"Baca laporan",allowedScopes:["fulfillmentShop"],requiresFreshMfa:false},
 ]};
 assert.deepEqual(invitationRole(fixture,shop,"store-staff",[]),{templateId:"store-staff",financePacks:[],additionalPermissions:[],delegablePermissions:[]});
 assert.equal(invitationRole(fixture,shop,"store-staff",["reporting"]),null);
});

test("ACC01 expiry uses Jakarta wall time and rejects expired or reversed periods",()=>{
 const now=new Date("2026-09-28T02:00:00Z");
 assert.deepEqual(invitationDates("2026-09-29T09:00","2026-10-01T09:00",now),{expiresAt:"2026-09-29T02:00:00.000Z",accessExpiresAt:"2026-10-01T02:00:00.000Z"});
 assert.equal(invitationDates("2026-10-06T09:00","",now),null);
 assert.equal(invitationDates("2026-09-28T08:00","",now),null);
 assert.equal(invitationDates("2026-09-29T09:00","2026-09-29T08:00",now),null);
});
