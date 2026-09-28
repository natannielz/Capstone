import {AccessFailure, accessMessage} from "../administration/workflow";

export type AttributeDraft = {key:string;value:string};
export type VariantDraft = {
 code:string; physicalItemKey:string; packageLabel:string; packageKind:string; baseUnitCode:string;
 baseUnitsPerPackage:string; minimumOrderPackages:string; orderIncrementPackages:string; attributes:AttributeDraft[];
};
export type PackageValues = {
 packageLabel:string; packageKind:"loose"|"sealed"|"other"; baseUnitCode:string; baseUnitsPerPackage:number;
 attributes:Record<string,string>; imageAssetIds:string[]; minimumOrderPackages:number; orderIncrementPackages:number;
};
const invalid=():never=>{throw new AccessFailure(400,"catalog_input_invalid");};
export function catalogText(value:string,maximum:number,empty=false):string{
 if(typeof value!=="string"||/[\u0000-\u001f\u007f-\u009f]/.test(value)||!value.isWellFormed())return invalid();
 const trimmed=value.trim();if((!empty&&!trimmed)||trimmed.length>maximum)return invalid();return trimmed;
}
export function catalogCode(value:string):string{
 const result=catalogText(value,64);if(!/^[A-Za-z0-9._-]+$/.test(result))return invalid();return result.toUpperCase();
}
function integer(value:string,maximum:number):number{
 if(typeof value!=="string"||!/^[1-9][0-9]*$/.test(value))return invalid();
 const parsed=Number(value);if(!Number.isSafeInteger(parsed)||parsed>maximum)return invalid();return parsed;
}
export function blankVariantDraft():VariantDraft{
 return {code:"",physicalItemKey:"",packageLabel:"",packageKind:"",baseUnitCode:"",baseUnitsPerPackage:"",minimumOrderPackages:"",orderIncrementPackages:"",attributes:[]};
}
export function packageValues(draft:VariantDraft):PackageValues{
 const kind=catalogText(draft.packageKind,16);if(kind!=="loose"&&kind!=="sealed"&&kind!=="other")return invalid();
 if(!Array.isArray(draft.attributes)||draft.attributes.length>32)return invalid();
 const pairs=draft.attributes.map(item=>[catalogText(item.key,64),catalogText(item.value,256)] as const);
 if(new Set(pairs.map(([key])=>key)).size!==pairs.length)return invalid();
 pairs.sort(([left],[right])=>left<right?-1:left>right?1:0);
 return {packageLabel:catalogText(draft.packageLabel,160),packageKind:kind,baseUnitCode:catalogText(draft.baseUnitCode,64),baseUnitsPerPackage:integer(draft.baseUnitsPerPackage,2147483647),
  attributes:Object.fromEntries(pairs),imageAssetIds:[],minimumOrderPackages:integer(draft.minimumOrderPackages,1000000),orderIncrementPackages:integer(draft.orderIncrementPackages,1000000)};
}
export function materialDefinitionChange(current:PackageValues,next:PackageValues):boolean{
 return current.packageKind!==next.packageKind||current.baseUnitCode!==next.baseUnitCode||current.baseUnitsPerPackage!==next.baseUnitsPerPackage||
  Object.keys(current.attributes).length!==Object.keys(next.attributes).length||Object.entries(current.attributes).some(([key,value])=>!Object.hasOwn(next.attributes,key)||next.attributes[key]!==value);
}
/** Abort transport and fence its result independently when an actor, owner or selection changes. */
export class CatalogReadGate{
 private request:{actorId:string;ownerKey:string;controller:AbortController}|null=null;
 begin(actorId:string,ownerKey:string){this.cancel();const request={actorId,ownerKey,controller:new AbortController()};this.request=request;return {signal:request.controller.signal,current:()=>this.request===request&&!request.controller.signal.aborted};}
 cancel(){this.request?.controller.abort();this.request=null;}
}
/** A pending attempt never borrows mutable form objects; persistent recovery stores references only. */
export function freezeCatalogAttempt<T>(value:T):Readonly<T>{
 const freeze=(item:unknown)=>{if(item!==null&&typeof item==="object"){Object.values(item).forEach(freeze);Object.freeze(item);}};
 const copy=structuredClone(value);freeze(copy);return copy;
}
export function catalogMessage(error:unknown):string{
 if(error instanceof AccessFailure){
  if(error.code==="catalog_input_invalid")return "Lengkapi data kemasan dengan jumlah bulat yang jelas. Kode dan atribut harus sah serta tidak berulang.";
  if(error.code==="catalog_response_invalid")return "Data atau hasil yang diterima belum sesuai. Muat ulang data; referensi permintaan yang belum pasti tetap disimpan.";
  if(error.code==="catalog_identifier_conflict"||error.code==="catalog_replacement_identity_reused")return "Kode tersebut sudah digunakan. Gunakan kode baru yang belum pernah dipakai untuk identitas barang lain.";
  if(error.code==="catalog_revision_exhausted")return "Data ini belum dapat diperbarui. Hubungi pengelola katalog dengan referensi bantuan.";
  if(error.code==="invalid_catalog_input"||error.code==="invalid_sku_code")return "Periksa kembali nama, kode, dan seluruh isian kemasan sebelum menyimpan.";
  if(error.code==="catalog_material_change")return "Isi atau sifat barang berubah. Buat SKU pengganti agar identitas dan riwayat kemasan lama tetap utuh.";
  if(error.code==="catalog_revision_conflict"||error.code==="version_conflict")return "Data berubah sejak dibuka. Muat data terbaru dan periksa ulang sebelum menyimpan.";
 }
 return accessMessage(error);
}
