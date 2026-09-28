import {AccessFailure,stableContent} from '../administration/workflow';
import type {OperationReference,CatalogRecoveryReceipt} from '../administration/operation-journal';
import {validReference,type AccessScope} from '../organizations/access-scope';
import {catalogText,packageValues,type VariantDraft} from './catalog-editor-state';

export const catalogPath='/administration/catalog';
export const catalogOperations=['catalog.category.save','catalog.family.create','catalog.family.edit','catalog.sku.create','catalog.sku.revise','catalog.sku.replace','catalog.sku.retire','catalog.sku.publish'] as const;
export type CatalogOperation=typeof catalogOperations[number];
const invalid=():never=>{throw new AccessFailure(502,'catalog_response_invalid');};
const inputInvalid=():never=>{throw new AccessFailure(400,'catalog_input_invalid');};
const record=(value:unknown):Record<string,unknown>=>value!==null&&typeof value==='object'&&!Array.isArray(value)?value as Record<string,unknown>:invalid();
const id=(value:unknown):string=>typeof value==='string'&&value===value.trim()&&validReference(value)?value:invalid();
const nullableId=(value:unknown):string|null=>value===null?null:id(value);
function text(value:unknown,max:number,empty=false):string {if(typeof value!=='string')return invalid();try{const parsed=catalogText(value,max,empty);return parsed===value?parsed:invalid();}catch{return invalid();}}
const boolean=(value:unknown):boolean=>typeof value==='boolean'?value:invalid();
const time=(value:unknown):string=>typeof value==='string'&&value.length<=40&&/^\d{4}-\d\d-\d\dT/.test(value)&&Number.isFinite(Date.parse(value))?value:invalid();
const uuid=(value:unknown):string=>typeof value==='string'&&value.length===36&&/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(value)&&value!=='00000000-0000-0000-0000-000000000000'?value:invalid();
function list<T>(value:unknown,maximum:number,parse:(item:unknown)=>T):T[]{return Array.isArray(value)&&value.length<=maximum?value.map(parse):invalid();}
function unique<T>(items:T[],key:(item:T)=>string):T[]{return new Set(items.map(key)).size===items.length?items:invalid();}
const positive=(value:unknown,max:number):number=>typeof value==='number'&&Number.isInteger(value)&&value>=1&&value<=max?value:invalid();
const status=(value:unknown):'active'|'suspended'|'revoked'=>value==='active'||value==='suspended'||value==='revoked'?value:invalid();
export function parseCatalogOwner(value:unknown):AccessScope{
 const v=record(value);if(v.kind!=='fulfillmentShop'&&v.kind!=='fulfillmentWarehouse'||v.branchId!==null||v.divisionId!==null)return invalid();
 const warehouseId=v.kind==='fulfillmentWarehouse'?id(v.warehouseId):v.warehouseId===null?null:invalid();
 return {kind:v.kind,organizationId:id(v.organizationId),branchId:null,divisionId:null,shopId:id(v.shopId),warehouseId,channelId:id(v.channelId)};
}
export const ownerKey=(owner:AccessScope)=>stableContent(owner);
export function parseCatalogOwnerOptions(value:unknown){return unique(list(record(value).items,256,item=>{const v=record(item);return {owner:parseCatalogOwner(v.owner),label:text(v.label,500)};}),item=>ownerKey(item.owner));}
export type CatalogOwnerOption=ReturnType<typeof parseCatalogOwnerOptions>[number];
function base(value:unknown){const v=record(value),owner=parseCatalogOwner(v.owner),organizationId=id(v.organizationId);if(owner.organizationId!==organizationId)return invalid();return {id:id(v.id),organizationId,owner,active:boolean(v.active),version:id(v.version)};}
export function parseCatalogCategory(value:unknown){const v=record(value);const code=text(v.code,64);if(!/^[A-Z0-9._-]+$/.test(code))return invalid();return {...base(v),code,name:text(v.name,160)};}
export type CatalogCategory=ReturnType<typeof parseCatalogCategory>;
export function catalogDescription(value:string):string{
 if(typeof value!=='string'||/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f]/.test(value)||!value.isWellFormed())return inputInvalid();
 const result=value.replace(/\r\n?/g,'\n').trim();return result.length<=4000?result:inputInvalid();
}
export function parseCatalogFamily(value:unknown){const v=record(value);if(typeof v.description!=='string')return invalid();let description:string;try{description=catalogDescription(v.description);}catch{return invalid();}if(description!==v.description)return invalid();
 return {...base(v),publicId:uuid(v.publicId),categoryId:id(v.categoryId),categoryCode:text(v.categoryCode,64),name:text(v.name,160),description};}
export type CatalogFamily=ReturnType<typeof parseCatalogFamily>;
export function parseCatalogDefinition(value:unknown){const v=record(value),packageKind=v.packageKind;if(packageKind!=='loose'&&packageKind!=='sealed'&&packageKind!=='other')return invalid();
 const attributes=unique(list(v.attributes,32,item=>{const a=record(item);return {key:text(a.key,64),value:text(a.value,256)};}),a=>a.key);
 return {id:id(v.id),skuId:id(v.skuId),version:id(v.version),packageLabel:text(v.packageLabel,160),packageKind,baseUnitCode:text(v.baseUnitCode,64),baseUnitsPerPackage:positive(v.baseUnitsPerPackage,2147483647),attributes,
 minimumOrderPackages:positive(v.minimumOrderPackages,1000000),orderIncrementPackages:positive(v.orderIncrementPackages,1000000),effectiveAt:time(v.effectiveAt)};
}
export function parseCatalogSku(value:unknown){const v=record(value),common=base(v),definition=parseCatalogDefinition(v.definition),currentDefinitionId=id(v.currentDefinitionId),predecessorSkuId=nullableId(v.predecessorSkuId),code=text(v.code,64);
 if(definition.skuId!==common.id||definition.id!==currentDefinitionId||predecessorSkuId===common.id||!/^[A-Z0-9._-]+$/.test(code))return invalid();
 const publications=unique(list(v.publications,128,item=>{const p=record(item),owner=parseCatalogOwner(p.owner);if(owner.organizationId!==common.organizationId)return invalid();return {assortmentId:id(p.assortmentId),owner,version:id(p.version),status:status(p.status)};}),p=>p.assortmentId);
 if(!common.active&&publications.length)return invalid();
 return {...common,familyId:id(v.familyId),publicId:uuid(v.publicId),code,physicalItemKey:text(v.physicalItemKey,128),currentDefinitionId,predecessorSkuId,definition,publications};
}
export type CatalogSku=ReturnType<typeof parseCatalogSku>;
export function parseCatalogAssortment(value:unknown){const v=record(value);if(v.status!=='active')return invalid();return {id:id(v.id),owner:parseCatalogOwner(v.owner),version:id(v.version),status:status(v.status),label:text(v.label,500)};}
export type CatalogAssortment=ReturnType<typeof parseCatalogAssortment>;
export type CatalogPage<T>={total:string;items:T[]};
export function parseCatalogPage<T extends {id:string}>(value:unknown,parse:(item:unknown)=>T):CatalogPage<T>{const v=record(value),total=v.total==='0'?'0':id(v.total),items=unique(list(v.items,100,parse),item=>item.id);if(BigInt(total)<BigInt(items.length))return invalid();return {total,items};}
export function definitionInput(draft:VariantDraft){const values=packageValues(draft);return {...values,attributes:Object.entries(values.attributes).map(([key,value])=>({key,value}))};}
export function variantDraft(sku:CatalogSku):VariantDraft{const d=sku.definition;return {code:sku.code,physicalItemKey:sku.physicalItemKey,packageLabel:d.packageLabel,packageKind:d.packageKind,baseUnitCode:d.baseUnitCode,baseUnitsPerPackage:String(d.baseUnitsPerPackage),minimumOrderPackages:String(d.minimumOrderPackages),orderIncrementPackages:String(d.orderIncrementPackages),attributes:d.attributes.map(item=>({...item}))};}
export function publicationSelection(sku:CatalogSku,options:CatalogAssortment[],desired:string[]){
 if(desired.length>128||new Set(desired).size!==desired.length)return inputInvalid();
 const versions=new Map(sku.publications.map(p=>[p.assortmentId,p.version]));
 for(const selected of desired){const option=options.find(item=>item.id===selected);if(!option||option.status!=='active')return inputInvalid();if(!versions.has(selected))versions.set(selected,option.version);}
 if(versions.size>256)return inputInvalid();const compare=(a:string,b:string)=>BigInt(a)<BigInt(b)?-1:BigInt(a)>BigInt(b)?1:0;
 return {assortmentIds:[...desired].sort(compare),expectedAssortments:[...versions].sort(([a],[b])=>compare(a,b)).map(([id,version])=>({id,version}))};
}
export type CatalogExpected={id?:string;familyId?:string;predecessorSkuId?:string};
export function catalogOutcome(value:unknown,operation:string,expected:CatalogExpected={}){
 if(!catalogOperations.some(item=>item===operation))return invalid();
 const v=record(value),kind=operation==='catalog.category.save'?'category':operation.startsWith('catalog.family.')?'family':'sku';
 const result={kind:text(v.kind,16),id:id(v.id),version:id(v.version),familyId:nullableId(v.familyId),currentDefinitionId:nullableId(v.currentDefinitionId),definitionVersion:nullableId(v.definitionVersion),predecessorSkuId:nullableId(v.predecessorSkuId),recordedAt:time(v.recordedAt)};
 if(result.kind!==kind||expected.id&&result.id!==expected.id||expected.familyId&&result.familyId!==expected.familyId||expected.predecessorSkuId&&result.predecessorSkuId!==expected.predecessorSkuId)return invalid();
 if(kind==='category'&&(result.familyId!==null||result.currentDefinitionId!==null||result.definitionVersion!==null||result.predecessorSkuId!==null))return invalid();
 if(kind==='family'&&(result.familyId!==result.id||result.currentDefinitionId!==null||result.definitionVersion!==null||result.predecessorSkuId!==null))return invalid();
 if(kind==='sku'&&(result.familyId===null||result.currentDefinitionId===null||result.definitionVersion===null||result.predecessorSkuId===result.id))return invalid();
 if(operation==='catalog.sku.replace'&&result.predecessorSkuId===null||operation==='catalog.sku.create'&&result.predecessorSkuId!==null)return invalid();
 return result;
}
export type CatalogResult=ReturnType<typeof catalogOutcome>;
export function catalogReceipt(value:unknown,entry:OperationReference,expected:CatalogExpected={}):CatalogRecoveryReceipt|null{
 if(!catalogOperations.some(item=>item===entry.operation))return invalid();const v=record(value);
 if(v.state==='not_confirmed'){if(v.result!==null||v.recordedAt!==null)return invalid();return null;}
 if(v.state!=='committed')return invalid();const result=catalogOutcome(v.result,entry.operation,expected),recordedAt=time(v.recordedAt);if(Date.parse(recordedAt)!==Date.parse(result.recordedAt))return invalid();
 return {state:'committed',result:{id:result.id,version:result.version},recordedAt};
}

export type CatalogCommandBase={actorId:string;ownerKey:string;reason:string;expected:CatalogExpected};
export type CatalogCommand=CatalogCommandBase & (
 {operation:'catalog.category.save';version:string;body:{owner:AccessScope;categoryId:string|null;code:string;name:string}}|
 {operation:'catalog.family.create';body:{owner:AccessScope;categoryId:string;name:string;description:string;imageAssetIds:string[]}}|
 {operation:'catalog.family.edit';id:string;version:string;body:{categoryId:string;name:string;description:string;imageAssetIds:string[]}}|
 {operation:'catalog.sku.create';id:string;version:string;body:{code:string;physicalItemKey:string;definition:ReturnType<typeof definitionInput>}}|
 {operation:'catalog.sku.revise';id:string;version:string;body:{definition:ReturnType<typeof definitionInput>}}|
 {operation:'catalog.sku.replace';id:string;version:string;body:{code:string;physicalItemKey:string;definition:ReturnType<typeof definitionInput>}}|
 {operation:'catalog.sku.retire';id:string;version:string;body:Record<string,never>}|
 {operation:'catalog.sku.publish';id:string;version:string;body:ReturnType<typeof publicationSelection>}
);
