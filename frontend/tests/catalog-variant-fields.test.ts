import {test} from "node:test";
import assert from "node:assert/strict";
import {createElement} from "react";
import {renderToStaticMarkup} from "react-dom/server";
import {CatalogVariantFields} from "../src/modules/catalog/CatalogVariantFields";
import {blankVariantDraft} from "../src/modules/catalog/catalog-editor-state";

test("blank variant controls explain explicit packaging without selecting or guessing quantities",()=>{
 const html=renderToStaticMarkup(createElement(CatalogVariantFields,{draft:blankVariantDraft(),mode:"create",disabled:false,onChange(){}}));
 assert.match(html,/Kode SKU/);assert.match(html,/Isi unit dasar per kemasan/);assert.match(html,/Pilih jenis kemasan/);
 assert.doesNotMatch(html,/value="(?:12|24|1)"/);assert.doesNotMatch(html,/type="number"/);assert.match(html,/inputMode="numeric"/);
});

test("cosmetic definition editing locks material identity and shows the replacement path",()=>{
 const draft={...blankVariantDraft(),code:"ATK-12",physicalItemKey:"PENA-BIRU",packageLabel:"Dus",packageKind:"sealed",baseUnitCode:"PCS",baseUnitsPerPackage:"12",minimumOrderPackages:"1",orderIncrementPackages:"1",attributes:[{key:"warna",value:"biru"}]};
 const html=renderToStaticMarkup(createElement(CatalogVariantFields,{draft,mode:"revise",disabled:false,onChange(){}}));
 assert.match(html,/<input[^>]*disabled[^>]*value="ATK-12"/);assert.match(html,/<input[^>]*disabled[^>]*value="12"/);
 assert.match(html,/SKU pengganti/);assert.match(html,/Sifat barang/);
 assert.doesNotMatch(html,/<input[^>]*disabled[^>]*value="Dus"/);
});

test('package kind has an explicit label that excludes option text',()=>{const html=renderToStaticMarkup(createElement(CatalogVariantFields,{draft:blankVariantDraft(),mode:'create',disabled:false,onChange(){}}));assert.match(html,/<select[^>]*aria-label="Jenis kemasan"/);});
