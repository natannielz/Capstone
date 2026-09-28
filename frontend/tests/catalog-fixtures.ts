export const actorId='9007199254740993',familyId='9007199254740995',skuId='9007199254740997',version='9007199254740999';
export const owner={kind:'fulfillmentShop' as const,organizationId:'1',branchId:null,divisionId:null,shopId:'10',warehouseId:null,channelId:'20'};
export const category={id:'30',organizationId:'1',owner,code:'ATK',name:'Alat tulis',active:true,version:'1'};
export const family={id:familyId,organizationId:'1',owner,publicId:'12345678-1234-4123-8123-123456789abc',categoryId:'30',categoryCode:'ATK',name:'Pena biru',description:'Pena untuk kantor',active:true,version};
export const definition={id:'51',skuId,version:'1',packageLabel:'Dus',packageKind:'sealed' as const,baseUnitCode:'PCS',baseUnitsPerPackage:12,attributes:[{key:'warna',value:'biru'}],minimumOrderPackages:1,orderIncrementPackages:1,effectiveAt:'2026-09-28T01:00:00Z'};
export const sku={id:skuId,organizationId:'1',familyId,publicId:'12345678-1234-4123-8123-123456789abd',code:'PENA-12',physicalItemKey:'Pena biru standar',active:true,currentDefinitionId:'51',version,predecessorSkuId:null,owner,definition,publications:[{assortmentId:'71',owner,version:'4',status:'active' as const}]};
export const result={kind:'sku',id:skuId,version,familyId,currentDefinitionId:'51',definitionVersion:'1',predecessorSkuId:null,recordedAt:'2026-09-28T01:10:00Z'};
export const entry={actorId,operation:'catalog.sku.revise',key:'original-key',createdAt:'2026-09-28T01:05:00Z'};
export const draft={code:'pena-12',physicalItemKey:'Pena biru standar',packageLabel:'Dus',packageKind:'sealed',baseUnitCode:'PCS',baseUnitsPerPackage:'12',minimumOrderPackages:'1',orderIncrementPackages:'1',attributes:[{key:'warna',value:'biru'}]};
