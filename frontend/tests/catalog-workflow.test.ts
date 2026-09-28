import {test} from 'node:test';
import assert from 'node:assert/strict';
import {parseCatalogOwnerOptions,parseCatalogSku,catalogReceipt,catalogOutcome,publicationSelection,definitionInput,catalogDescription} from '../src/modules/catalog/catalog-workflow';
import {owner,sku,result,entry,draft,familyId,skuId} from './catalog-fixtures';
test('only server returned fulfillment owners survive strict canonical projection',()=>{
 assert.deepEqual(parseCatalogOwnerOptions({items:[{owner,label:'Toko Tebet',privateBuyer:'hidden'}]}),[{owner,label:'Toko Tebet'}]);
 for(const changed of [{kind:'system'},{organizationId:1},{branchId:'4'},{warehouseId:'4'},{channelId:'01'}])assert.throws(()=>parseCatalogOwnerOptions({items:[{owner:{...owner,...changed},label:'Label'}]}),/catalog_response_invalid/);
 assert.throws(()=>parseCatalogOwnerOptions({items:[{owner,label:'A'},{owner,label:'B'}]}),/catalog_response_invalid/);
});
test('SKU parser retains exact long strings and facts while stripping private extras',()=>{
 const value=parseCatalogSku({...sku,secret:'hidden',definition:{...sku.definition,price:0}});
 assert.equal(value.id,skuId);assert.equal(value.definition.baseUnitsPerPackage,12);assert.equal('secret' in value,false);assert.equal('price' in value.definition,false);
 for(const change of [{version:1},{definition:{...sku.definition,skuId:'90'}},{currentDefinitionId:'60'},{predecessorSkuId:skuId},{definition:{...sku.definition,baseUnitsPerPackage:'12'}},{definition:{...sku.definition,attributes:[{key:'x',value:'a'},{key:'x',value:'b'}]}}])assert.throws(()=>parseCatalogSku({...sku,...change}),/catalog_response_invalid/);
});
test('definition serialization uses sorted array attributes and no image price or availability invention',()=>{
 const value=definitionInput({...draft,attributes:[{key:'z',value:'1'},{key:'__proto__',value:'ok'}]});assert.deepEqual(value.attributes,[{key:'__proto__',value:'ok'},{key:'z',value:'1'}]);assert.deepEqual(value.imageAssetIds,[]);assert.equal('price' in value,false);assert.equal('availability' in value,false);
});
test('family descriptions normalize line endings and preserve tabs without permitting controls',()=>{
 assert.equal(catalogDescription('  Baris satu\r\nBaris dua\tlanjut  '),'Baris satu\nBaris dua\tlanjut');assert.equal(catalogDescription(''), '');
 for(const value of ['x\u0000','x\u0085','x'.repeat(4001),'\ud800'])assert.throws(()=>catalogDescription(value),/catalog_input_invalid/);
});
test('publication replacement pins exact union with old versions winning over changed option versions',()=>{
 const next={id:'72',owner,version:'8',status:'active' as const,label:'Cabang lain'};
 assert.deepEqual(publicationSelection(sku,[next,{...next,id:'71',version:'99'}],['72']),{assortmentIds:['72'],expectedAssortments:[{id:'71',version:'4'},{id:'72',version:'8'}]});
 assert.throws(()=>publicationSelection(sku,[next],['99']),/catalog_input_invalid/);
 assert.throws(()=>publicationSelection(sku,[next],['72','72']),/catalog_input_invalid/);
 assert.deepEqual(publicationSelection(sku,[],[]),{assortmentIds:[],expectedAssortments:[{id:'71',version:'4'}]});
});
test('only exact typed committed receipt clears; absent outcome remains unknown',()=>{
 assert.equal(catalogReceipt({state:'not_confirmed',result:null,recordedAt:null},entry),null);
 assert.deepEqual(catalogReceipt({state:'committed',result,recordedAt:result.recordedAt},entry,{id:skuId,familyId}),{state:'committed',result:{id:skuId,version:result.version},recordedAt:result.recordedAt});
 for(const response of [null,{state:'closed_no_effect',result:null,recordedAt:result.recordedAt},{state:'not_confirmed',result,recordedAt:null},{state:'committed',result,recordedAt:null},{state:'committed',result,recordedAt:'2026-09-28T01:11:00Z'},{state:'committed',result:{...result,version:1},recordedAt:result.recordedAt}])assert.throws(()=>catalogReceipt(response,entry),/catalog_response_invalid/);
 assert.throws(()=>catalogReceipt({state:'committed',result,recordedAt:result.recordedAt},{...entry,operation:'access.grant.propose'}),/catalog_response_invalid/);
});
test('outcome binds original action kind target family and replacement predecessor',()=>{
 assert.equal(catalogOutcome(result,'catalog.sku.create',{familyId}).id,skuId);
 for(const [operation,expected] of [['catalog.category.save',{}],['catalog.family.edit',{}],['catalog.sku.revise',{id:'99'}],['catalog.sku.create',{familyId:'99'}],['catalog.sku.replace',{predecessorSkuId:'90'}]] as const)assert.throws(()=>catalogOutcome(result,operation,expected),/catalog_response_invalid/);
 assert.throws(()=>catalogOutcome({...result,kind:'category'},'catalog.category.save',{}),/catalog_response_invalid/);
});

test('persisted public identities are canonical v4 and retired SKUs have no publications',()=>{
 for(const publicId of ['12345678-1234-1123-8123-123456789abc','12345678-1234-4123-7123-123456789abc','12345678-1234-4123-B123-123456789abc'])assert.throws(()=>parseCatalogSku({...sku,publicId}),/catalog_response_invalid/);
 assert.throws(()=>parseCatalogSku({...sku,publications:Array.from({length:129},(_,i)=>({...sku.publications[0],assortmentId:String(i+100)}))}),/catalog_response_invalid/);
 assert.throws(()=>parseCatalogSku({...sku,active:false}),/catalog_response_invalid/);
 assert.equal(parseCatalogSku({...sku,active:false,publications:[]}).active,false);
});

test('public UUID response rejects trailing newline even when the regex end anchor could match',()=>{assert.throws(()=>parseCatalogSku({...sku,publicId:sku.publicId+'\n'}),/catalog_response_invalid/);});
test('response decimal references reject trailing whitespace rather than normalizing it',()=>{for(const changed of [{id:'1\n'},{version:'1\n'},{familyId:'1\n'},{currentDefinitionId:'1\n'}])assert.throws(()=>parseCatalogSku({...sku,...changed}),/catalog_response_invalid/);});
