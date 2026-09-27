import type { components } from "../../api/generated/schema";
import { scopeKindLabels, validReference } from "../organizations/access-scope";
import { organizationKinds } from "../organizations/organization-workflow";

type Schemas = components["schemas"];
type Scope = Schemas["AccessScopeDto"];
type Node = Schemas["AccessNodeDto"];
type Kind = Schemas["GrantScopeKind"];
export type InvitationScopeOption = { key:string; scope:Scope; label:string };

export function invitationScopeOptions(current:Schemas["CurrentAccessDto"], labels:Readonly<Record<string,string>>={}):InvitationScopeOption[]{
  if(current.status!=="active")return [];
  const counts=new Map<string,number>();
  for(const node of current.scopes)counts.set(node.id,(counts.get(node.id)??0)+1);
  const nodes=current.scopes.filter(node=>node.status==="active"&&validReference(node.id)&&counts.get(node.id)===1);
  const roots=(kind:Node["kind"])=>nodes.filter(node=>node.kind===kind&&node.parentId===null);
  const children=(parent:Node,kind:Node["kind"])=>nodes.filter(node=>node.kind===kind&&node.parentId===parent.id);
  const named=(node:Node)=>labels[node.id]?.trim()||`${organizationKinds[node.kind]} ${node.id} (nama belum tersedia)`;
  const options:InvitationScopeOption[]=[];
  const add=(scope:Scope,path:Node[])=>options.push({key:JSON.stringify(scope),scope,label:`${scopeKindLabels[scope.kind]}: ${path.map(named).join(" → ")}`});
  for(const org of roots("buyingOrganization")){
    add({kind:"buyingOrganization",organizationId:org.id},[org]);
    add({kind:"buyerOwned",organizationId:org.id},[org]);
    for(const branch of children(org,"branch")){
      add({kind:"buyingBranch",organizationId:org.id,branchId:branch.id},[org,branch]);
      add({kind:"buyerOwned",organizationId:org.id,branchId:branch.id},[org,branch]);
      for(const division of children(branch,"division")){
        add({kind:"buyingDivision",organizationId:org.id,branchId:branch.id,divisionId:division.id},[org,branch,division]);
        add({kind:"buyerOwned",organizationId:org.id,branchId:branch.id,divisionId:division.id},[org,branch,division]);
      }
    }
  }
  for(const org of roots("fulfillmentOrganization"))for(const shop of children(org,"shop"))for(const channel of children(org,"channel")){
    add({kind:"fulfillmentShop",organizationId:org.id,shopId:shop.id,channelId:channel.id},[org,shop,channel]);
    add({kind:"courierAssignment",organizationId:org.id,shopId:shop.id,channelId:channel.id},[org,shop,channel]);
    for(const warehouse of children(shop,"warehouse")){
      add({kind:"fulfillmentWarehouse",organizationId:org.id,shopId:shop.id,warehouseId:warehouse.id,channelId:channel.id},[org,shop,warehouse,channel]);
      add({kind:"courierAssignment",organizationId:org.id,shopId:shop.id,warehouseId:warehouse.id,channelId:channel.id},[org,shop,warehouse,channel]);
    }
  }
  if(current.grants.some(grant=>grant.status==="active"&&grant.scope.kind==="system"))add({kind:"system"},[]);
  return options;
}

export function invitationRole(catalog:Schemas["AccessCatalogDto"],scope:Scope,templateId:string,financePacks:readonly string[]){
  const template=catalog.roles.find(role=>role.id===templateId);
  if(!template||financePacks.length>4||financePacks.length>0&&templateId!=="finance"||new Set(financePacks).size!==financePacks.length)return null;
  const packs=financePacks.map(id=>catalog.financePacks.find(pack=>pack.id===id));
  if(packs.some(pack=>!pack))return null;
  const permissions=[...template.permissions,...packs.flatMap(pack=>pack?.permissions??[])];
  if(permissions.some(id=>!catalog.permissions.find(permission=>permission.id===id)?.allowedScopes.includes(scope.kind)))return null;
  const business=permissions.some(id=>id!=="access.approve"&&id!=="access.revoke"&&id!=="membership.approve");
  if(business&&((scope.kind==="buyingBranch"||scope.kind==="buyingDivision")&&!permissions.includes("branch.pic")||
    scope.kind==="buyingOrganization"&&!permissions.includes("organization.wide")))return null;
  return {templateId,financePacks:[...financePacks].sort(),additionalPermissions:[],delegablePermissions:[]};
}

export function invitationDates(inviteLocal:string,accessLocal:string,now=new Date()){
  const parse=(value:string)=>/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(value)?new Date(`${value}:00+07:00`):null;
  const invite=parse(inviteLocal),access=accessLocal?parse(accessLocal):null;
  if(!invite||Number.isNaN(invite.getTime())||invite<=now||invite.getTime()>now.getTime()+7*86_400_000||accessLocal&&(!access||Number.isNaN(access.getTime())||access<=invite))return null;
  return {expiresAt:invite.toISOString(),accessExpiresAt:access?.toISOString()??null};
}

export const invitationScopeKinds:Kind[]=["buyerOwned","buyingDivision","buyingBranch","buyingOrganization","fulfillmentShop","fulfillmentWarehouse","courierAssignment","system"];
