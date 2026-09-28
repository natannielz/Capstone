import {test} from "node:test";
import assert from "node:assert/strict";
import {blankVariantDraft, packageValues, catalogCode, materialDefinitionChange, CatalogReadGate, freezeCatalogAttempt} from "../src/modules/catalog/catalog-editor-state";

const draft={code:" atk-12 ",physicalItemKey:"Pena biru ukuran standar",packageLabel:"Dus",packageKind:"sealed",baseUnitCode:"PCS",baseUnitsPerPackage:"12",minimumOrderPackages:"1",orderIncrementPackages:"1",attributes:[{key:"warna",value:"biru"}]};

test("a new variant has no inferred package facts, code or commercial defaults",()=>{
 const blank=blankVariantDraft();
 for(const field of ["code","physicalItemKey","packageLabel","packageKind","baseUnitCode","baseUnitsPerPackage","minimumOrderPackages","orderIncrementPackages"] as const)assert.equal(blank[field],"");
 assert.deepEqual(blank.attributes,[]);
 assert.throws(()=>packageValues({...blank,packageLabel:"Dus 24"}),/catalog_input_invalid/);
 assert.throws(()=>packageValues({...draft,baseUnitsPerPackage:""}),/catalog_input_invalid/);
});

test("explicit 12 and 24 contents stay distinct despite equal package labels",()=>{
 const twelve=packageValues(draft),twentyFour=packageValues({...draft,baseUnitsPerPackage:"24"});
 assert.equal(twelve.baseUnitsPerPackage,12);assert.equal(twentyFour.baseUnitsPerPackage,24);assert.equal(twelve.packageLabel,"Dus");assert.equal(twentyFour.packageLabel,"Dus");
 assert.deepEqual(twelve.imageAssetIds,[]);assert.equal("price" in twelve,false);assert.equal("availability" in twelve,false);
});

test("contents use positive Int32 while MOQ and increment use their own million bound",()=>{
 assert.equal(packageValues({...draft,baseUnitsPerPackage:"2147483647"}).baseUnitsPerPackage,2147483647);
 for(const field of ["baseUnitsPerPackage","minimumOrderPackages","orderIncrementPackages"] as const){
  for(const value of ["0","-1","1.5","1e2","+12"," 12 ","٠١","01",""])assert.throws(()=>packageValues({...draft,[field]:value}),/catalog_input_invalid/);
 }
 assert.throws(()=>packageValues({...draft,baseUnitsPerPackage:"2147483648"}),/catalog_input_invalid/);
 for(const field of ["minimumOrderPackages","orderIncrementPackages"] as const){assert.equal(packageValues({...draft,[field]:"1000000"})[field],1000000);assert.throws(()=>packageValues({...draft,[field]:"1000001"}),/catalog_input_invalid/);}
});

test("Unicode descriptions stay intact while normalized attribute collisions and controls are rejected",()=>{
 const value=packageValues({...draft,packageLabel:"  Kemasan isi dua belas – biru  ",attributes:[{key:" ukuran ",value:" besar "},{key:"Warna",value:"Biru"}]});
 assert.equal(value.packageLabel,"Kemasan isi dua belas – biru");assert.deepEqual(value.attributes,{Warna:"Biru",ukuran:"besar"});
 assert.throws(()=>packageValues({...draft,attributes:[{key:" warna ",value:"biru"},{key:"warna",value:"merah"}]}),/catalog_input_invalid/);
 assert.throws(()=>packageValues({...draft,attributes:Array.from({length:33},(_,i)=>({key:String(i),value:"x"}))}),/catalog_input_invalid/);
 for(const value of ["\u0000","\u0085","\ud800"])assert.throws(()=>packageValues({...draft,packageLabel:value}),/catalog_input_invalid/);
 assert.deepEqual(packageValues({...draft,attributes:[{key:"__proto__",value:"aman"}]}).attributes,{["__proto__"]:"aman"});
});

test("codes normalize ASCII only and never allocate identifiers from product names",()=>{
 assert.equal(catalogCode(" atk-12 "),"ATK-12");
 for(const code of ["","ATK 12","ＡＴＫ","atı","code\n","A".repeat(65)])assert.throws(()=>catalogCode(code),/catalog_input_invalid/);
});

test("cosmetic revisions preserve identity while changed package facts require replacement",()=>{
 const current=packageValues(draft);
 assert.equal(materialDefinitionChange(current,packageValues({...draft,packageLabel:"Dus biru",minimumOrderPackages:"2",orderIncrementPackages:"2"})),false);
 for(const changed of [{packageKind:"other"},{baseUnitCode:"UNIT"},{baseUnitsPerPackage:"10"},{attributes:[{key:"warna",value:"merah"}]}])assert.equal(materialDefinitionChange(current,packageValues({...draft,...changed})),true);
});

test("changing owner or actor invalidates pending private responses even if transport ignores abort",()=>{
 const gate=new CatalogReadGate();const first=gate.begin("9007199254740993","owner-a");const second=gate.begin("9007199254740993","owner-b");
 assert.equal(first.signal.aborted,true);assert.equal(first.current(),false);assert.equal(second.current(),true);
 const third=gate.begin("71","owner-b");assert.equal(second.current(),false);assert.equal(third.current(),true);gate.cancel();assert.equal(third.current(),false);
});

test("an uncertain attempt snapshots actor, owner, revisions and nested attributes without mutable aliases",()=>{
 const original={actorId:"9007199254740993",ownerKey:"owner-a",operation:"catalog.sku.create",payload:{familyId:"9007199254740995",revision:"9007199254740997",definition:packageValues(draft)}};
 const frozen=freezeCatalogAttempt(original);original.payload.definition.attributes.warna="merah";original.ownerKey="owner-b";
 assert.equal(frozen.ownerKey,"owner-a");assert.equal(frozen.payload.familyId,"9007199254740995");assert.equal(frozen.payload.revision,"9007199254740997");assert.equal(frozen.payload.definition.attributes.warna,"biru");
 assert.ok(Object.isFrozen(frozen));assert.ok(Object.isFrozen(frozen.payload.definition.attributes));
});
