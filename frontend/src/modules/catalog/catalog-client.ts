import {createUnitTokoClient} from '../../api/client';
import {AccessFailure} from '../administration/workflow';
import type {OperationReference} from '../administration/operation-journal';
import type {AccessScope} from '../organizations/access-scope';
import {parseCatalogOwnerOptions,parseCatalogCategory,parseCatalogFamily,parseCatalogSku,parseCatalogAssortment,parseCatalogPage,ownerKey,catalogOutcome,catalogReceipt,type CatalogCommand,type CatalogExpected} from './catalog-workflow';

type Client=ReturnType<typeof createUnitTokoClient>;
function value<T>(result:{data?:T;error?:unknown;response:Response}):T{
 if(!result.response.ok||result.data===undefined){const p=result.error&&typeof result.error==='object'?result.error as Record<string,unknown>:{};throw new AccessFailure(result.response.status,typeof p.code==='string'?p.code:'request_failed',typeof p.traceId==='string'?p.traceId:undefined);}return result.data;
}
const invalid=()=>{throw new AccessFailure(502,'catalog_response_invalid');};
/** Generated same-origin transport. Every command is sent once; only its owning editor can retry its exact snapshot. */
export function createCatalogGateway(client:Client=createUnitTokoClient()){
 return {
  async owners(actorId:string,signal?:AbortSignal){return parseCatalogOwnerOptions(value(await client.POST('/api/v1/catalog/administration/owners/query',{body:{purpose:'catalog-authoring'},params:{header:{'X-Expected-Actor-Id':actorId}},signal})));},
  async categories(actorId:string,owner:AccessScope,offset=0,signal?:AbortSignal){const page=parseCatalogPage(value(await client.POST('/api/v1/catalog/administration/categories/query',{body:{purpose:'catalog-authoring',owner,offset,limit:25},params:{header:{'X-Expected-Actor-Id':actorId}},signal})),parseCatalogCategory);if(page.items.some(item=>ownerKey(item.owner)!==ownerKey(owner)))invalid();return page;},
  async families(actorId:string,owner:AccessScope,search='',offset=0,signal?:AbortSignal){const page=parseCatalogPage(value(await client.POST('/api/v1/catalog/administration/families/query',{body:{purpose:'catalog-authoring',owner,search:search.trim(),offset,limit:25},params:{header:{'X-Expected-Actor-Id':actorId}},signal})),parseCatalogFamily);if(page.items.some(item=>ownerKey(item.owner)!==ownerKey(owner)))invalid();return page;},
  async family(actorId:string,id:string,signal?:AbortSignal){const result=parseCatalogFamily(value(await client.POST('/api/v1/catalog/administration/families/{id}/query',{body:{purpose:'catalog-authoring'},params:{path:{id},header:{'X-Expected-Actor-Id':actorId}},signal})));if(result.id!==id)invalid();return result;},
  async skus(actorId:string,id:string,offset=0,signal?:AbortSignal){const page=parseCatalogPage(value(await client.POST('/api/v1/catalog/administration/families/{id}/skus/query',{body:{purpose:'catalog-authoring',offset,limit:25},params:{path:{id},header:{'X-Expected-Actor-Id':actorId}},signal})),parseCatalogSku);if(page.items.some(item=>item.familyId!==id))invalid();return page;},
  async sku(actorId:string,id:string,signal?:AbortSignal){const result=parseCatalogSku(value(await client.POST('/api/v1/catalog/administration/skus/{id}/query',{body:{purpose:'catalog-authoring'},params:{path:{id},header:{'X-Expected-Actor-Id':actorId}},signal})));if(result.id!==id)invalid();return result;},
  async assortments(actorId:string,owner:AccessScope,offset=0,signal?:AbortSignal){const page=parseCatalogPage(value(await client.POST('/api/v1/catalog/administration/assortments/query',{body:{purpose:'catalog-authoring',owner,offset,limit:25},params:{header:{'X-Expected-Actor-Id':actorId}},signal})),parseCatalogAssortment);if(page.items.some(item=>item.owner.organizationId!==owner.organizationId))invalid();return page;},
  async submit(command:CatalogCommand,key:string,isCurrent:()=>boolean=()=>true){
   const header={'X-Expected-Actor-Id':command.actorId,'Idempotency-Key':key},reason=command.reason;let result:unknown;
   switch(command.operation){
    case 'catalog.category.save':result=value(await client.POST('/api/v1/catalog/administration/categories',{body:{...command.body,reason},params:{header:{...header,'If-Match':'"'+command.version+'"'}}}));break;
    case 'catalog.family.create':result=value(await client.POST('/api/v1/catalog/administration/families',{body:{...command.body,reason},params:{header}}));break;
    case 'catalog.family.edit':result=value(await client.POST('/api/v1/catalog/administration/families/{id}/edit',{body:{...command.body,reason},params:{path:{id:command.id},header:{...header,'If-Match':'"'+command.version+'"'}}}));break;
    case 'catalog.sku.create':result=value(await client.POST('/api/v1/catalog/administration/families/{id}/skus',{body:{...command.body,reason},params:{path:{id:command.id},header:{...header,'If-Match':'"'+command.version+'"'}}}));break;
    case 'catalog.sku.revise':result=value(await client.POST('/api/v1/catalog/administration/skus/{id}/definition',{body:{...command.body,reason},params:{path:{id:command.id},header:{...header,'If-Match':'"'+command.version+'"'}}}));break;
    case 'catalog.sku.replace':result=value(await client.POST('/api/v1/catalog/administration/skus/{id}/replacement',{body:{...command.body,reason},params:{path:{id:command.id},header:{...header,'If-Match':'"'+command.version+'"'}}}));break;
    case 'catalog.sku.retire':result=value(await client.POST('/api/v1/catalog/administration/skus/{id}/retire',{body:{reason},params:{path:{id:command.id},header:{...header,'If-Match':'"'+command.version+'"'}}}));break;
    case 'catalog.sku.publish':result=value(await client.POST('/api/v1/catalog/administration/skus/{id}/publications',{body:{...command.body,reason},params:{path:{id:command.id},header:{...header,'If-Match':'"'+command.version+'"'}}}));break;
   }
   if(!isCurrent())throw new AccessFailure(503,'commit_outcome_unknown');return catalogOutcome(result,command.operation,command.expected);
  },
  async receipt(entry:OperationReference,expected:CatalogExpected={}){return catalogReceipt(value(await client.POST('/api/v1/catalog/administration/operations/query',{body:{purpose:'operation-recovery',operation:entry.operation,key:entry.key},params:{header:{'X-Expected-Actor-Id':entry.actorId}}})),entry,expected);},
 };
}
export type CatalogGateway=ReturnType<typeof createCatalogGateway>;
