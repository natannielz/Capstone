'use client';
import {useState} from 'react';
import {AccessFailure} from '../administration/workflow';
import type {AccessScope} from '../organizations/access-scope';
import {catalogCode,catalogText,blankVariantDraft} from './catalog-editor-state';
import {catalogDescription,definitionInput,variantDraft,publicationSelection,ownerKey,type CatalogCategory,type CatalogFamily,type CatalogSku,type CatalogAssortment,type CatalogCommand,type CatalogOwnerOption} from './catalog-workflow';
import {CatalogVariantFields} from './CatalogVariantFields';
import common from '../administration/access.module.css';
import styles from './catalog-editor.module.css';

type Shared={actorId:string;owner:AccessScope;locked:boolean;onReview:(command:CatalogCommand)=>void;onError:(failure:unknown)=>void;onCancel:()=>void};
function Reason({value,onChange}:{value:string;onChange:(value:string)=>void}){return <label>Alasan perubahan<textarea required rows={2} maxLength={1000} value={value} onChange={event=>onChange(event.target.value)}/></label>;}
function Buttons({onCancel}:{onCancel:()=>void}){return <div className={common.actions}><button className={common.primary}>Periksa sebelum menyimpan</button><button type="button" className={common.secondary} onClick={onCancel}>Kembali</button></div>;}
export function CatalogCategoryEditor({actorId,owner,category,locked,onReview,onError,onCancel}:Shared&{category:CatalogCategory|null}){
 const [code,setCode]=useState(category?.code??''),[name,setName]=useState(category?.name??''),[reason,setReason]=useState('');
 return <form className={common.form} onSubmit={event=>{event.preventDefault();try{onReview({actorId,ownerKey:ownerKey(owner),operation:'catalog.category.save',version:category?.version??'0',reason:catalogText(reason,1000),expected:category?{id:category.id}:{},body:{owner,categoryId:category?.id??null,code:catalogCode(code),name:catalogText(name,160)}});}catch(error){onError(error);}}}>
 <h2>{category?'Ubah nama kategori':'Kategori baru'}</h2><fieldset className={common.fields} disabled={locked}>
 <label>Kode kategori<input required maxLength={64} disabled={!!category} value={code} onChange={event=>setCode(event.target.value)}/></label><p className={common.help}>Kode menjadi identitas tetap kategori. Nama dapat diperbaiki setelah tersimpan.</p>
 <label>Nama kategori<input required maxLength={160} value={name} onChange={event=>setName(event.target.value)}/></label><Reason value={reason} onChange={setReason}/><Buttons onCancel={onCancel}/></fieldset></form>;
}
export function CatalogFamilyEditor({actorId,owner,family,categories,locked,onReview,onError,onCancel}:Shared&{family:CatalogFamily|null;categories:CatalogCategory[]}){
 const [categoryId,setCategoryId]=useState(family?.categoryId??''),[name,setName]=useState(family?.name??''),[description,setDescription]=useState(family?.description??''),[reason,setReason]=useState('');
 return <form className={common.form} onSubmit={event=>{event.preventDefault();try{if(!categories.some(item=>item.id===categoryId&&item.active))throw new AccessFailure(400,'catalog_input_invalid');const base={actorId,ownerKey:ownerKey(owner),reason:catalogText(reason,1000),expected:family?{id:family.id,familyId:family.id}:{}};const body={categoryId,name:catalogText(name,160),description:catalogDescription(description),imageAssetIds:[]};onReview(family?{...base,operation:'catalog.family.edit',id:family.id,version:family.version,body}:{...base,operation:'catalog.family.create',body:{...body,owner}});}catch(error){onError(error);}}}>
 <h2>{family?'Ubah kelompok produk':'Kelompok produk baru'}</h2><p className={common.help}>Kelompok menyatukan varian dari satu produk. Setiap kemasan memiliki SKU dan isi yang dicatat sendiri.</p>
 <fieldset className={common.fields} disabled={locked}><label>Kategori produk<select aria-label="Kategori produk" required value={categoryId} onChange={event=>setCategoryId(event.target.value)}><option value="">Pilih kategori</option>{categories.filter(item=>item.active).map(item=><option key={item.id} value={item.id}>{item.code} — {item.name}</option>)}</select></label>
 {!categories.some(item=>item.active)&&<p className={styles.notice}>Tambahkan kategori pada cakupan ini terlebih dahulu.</p>}
 <label>Nama kelompok produk<input required maxLength={160} value={name} onChange={event=>setName(event.target.value)}/></label><label>Deskripsi produk<textarea rows={4} maxLength={4000} value={description} onChange={event=>setDescription(event.target.value)}/></label>
 <Reason value={reason} onChange={setReason}/><Buttons onCancel={onCancel}/></fieldset></form>;
}
export function CatalogSkuEditor({actorId,owner,family,sku,mode,locked,onReview,onError,onCancel}:Shared&{family:CatalogFamily;sku:CatalogSku|null;mode:'create'|'revise'|'replace'}){
 const [draft,setDraft]=useState(()=>{const value=sku?variantDraft(sku):blankVariantDraft();return mode==='replace'?{...value,code:''}:value;}),[reason,setReason]=useState('');
 return <form className={common.form} onSubmit={event=>{event.preventDefault();try{const base={actorId,ownerKey:ownerKey(owner),reason:catalogText(reason,1000)};const definition=definitionInput(draft);if(mode==='revise'&&sku)onReview({...base,operation:'catalog.sku.revise',id:sku.id,version:sku.version,expected:{id:sku.id,familyId:family.id},body:{definition}});else {const body={code:catalogCode(draft.code),physicalItemKey:catalogText(draft.physicalItemKey,128),definition};onReview(mode==='replace'&&sku?{...base,operation:'catalog.sku.replace',id:sku.id,version:sku.version,expected:{familyId:family.id,predecessorSkuId:sku.id},body}:{...base,operation:'catalog.sku.create',id:family.id,version:family.version,expected:{familyId:family.id},body});}}catch(error){onError(error);}}}>
 <h2>{mode==='create'?'Varian baru':mode==='replace'?'Buat SKU pengganti':'Ubah deskripsi kemasan'}</h2><p className={common.scopeSummary}>{family.name}{sku?' / '+sku.code:''}</p>
 <fieldset className={common.fields} disabled={locked}><CatalogVariantFields draft={draft} mode={mode} disabled={locked} onChange={setDraft}/><Reason value={reason} onChange={setReason}/><Buttons onCancel={onCancel}/></fieldset></form>;
}
export function CatalogRetirementEditor({actorId,owner,sku,locked,onReview,onError,onCancel}:Shared&{sku:CatalogSku}){
 const [reason,setReason]=useState(''),[confirmed,setConfirmed]=useState(false);
 return <form className={common.form} onSubmit={event=>{event.preventDefault();if(!confirmed)return;try{onReview({actorId,ownerKey:ownerKey(owner),operation:'catalog.sku.retire',id:sku.id,version:sku.version,reason:catalogText(reason,1000),expected:{id:sku.id,familyId:sku.familyId},body:{}});}catch(error){onError(error);}}}>
 <h2>Hentikan SKU {sku.code}</h2><p className={styles.notice}>SKU ini tidak dapat diterbitkan lagi. Seluruh {sku.publications.length} publikasi saat ini akan dilepas bersama-sama. Identitas, versi kemasan, dan riwayat lama tetap tersimpan.</p>
 <fieldset className={common.fields} disabled={locked}><label className={common.checkbox}><input type="checkbox" required checked={confirmed} onChange={event=>setConfirmed(event.target.checked)}/><span>Saya sudah memeriksa dampaknya dan ingin menghentikan SKU ini.</span></label><Reason value={reason} onChange={setReason}/><Buttons onCancel={onCancel}/></fieldset></form>;
}
export function CatalogPublicationEditor({actorId,owner,sku,options,owners,locked,onReview,onError,onCancel,onMore,hasMore,loading}:Shared&{sku:CatalogSku;options:CatalogAssortment[];owners:CatalogOwnerOption[];onMore:()=>void;hasMore:boolean;loading:boolean}){
 const [desired,setDesired]=useState(()=>sku.publications.map(item=>item.assortmentId)),[reason,setReason]=useState('');
 const unavailable=sku.publications.filter(item=>!options.some(option=>option.id===item.assortmentId));
 function toggle(id:string,checked:boolean){setDesired(value=>checked?[...value,id]:value.filter(item=>item!==id));}
 return <form className={common.form} onSubmit={event=>{event.preventDefault();try{onReview({actorId,ownerKey:ownerKey(owner),operation:'catalog.sku.publish',id:sku.id,version:sku.version,reason:catalogText(reason,1000),expected:{id:sku.id,familyId:sku.familyId},body:publicationSelection(sku,options,desired)});}catch(error){onError(error);}}}>
 <h2>Atur publikasi {sku.code}</h2><p>Publikasi menghubungkan SKU dengan daftar barang yang sudah disetujui. Publikasi tidak memberikan izin baru kepada pembeli.</p>
 <fieldset className={common.fields} disabled={locked||loading}><fieldset className={common.choiceGroup}><legend>Daftar barang tujuan</legend>
 {options.map(item=><label className={common.checkbox} key={item.id}><input type="checkbox" checked={desired.includes(item.id)} onChange={event=>toggle(item.id,event.target.checked)}/><span>{item.label}</span></label>)}
 {!options.length&&<p className={common.help}>Belum ada daftar barang aktif yang dapat dikelola pada cakupan ini. Penyimpanan draf produk tetap tersedia.</p>}
 {unavailable.map((item,index)=><label className={common.checkbox} key={item.assortmentId}><input type="checkbox" checked={desired.includes(item.assortmentId)} disabled={!desired.includes(item.assortmentId)} onChange={event=>toggle(item.assortmentId,event.target.checked)}/><span>{owners.find(option=>ownerKey(option.owner)===ownerKey(item.owner))?.label??'Publikasi tersimpan '+(index+1)} — belum ada dalam pilihan aktif. Muat pilihan berikutnya, atau lepas pilihan ini dengan sengaja.</span></label>)}
 </fieldset><p className={common.help}>{desired.length} tujuan dipilih. Tujuan yang dilepas tidak lagi memuat SKU ini. Pilihan kosong melepas seluruh publikasi.</p>
 {hasMore&&<button type="button" className={common.secondary} onClick={onMore}>Muat pilihan publikasi berikutnya</button>}
 <Reason value={reason} onChange={setReason}/><Buttons onCancel={onCancel}/></fieldset>{loading&&<p role="status">Memuat pilihan publikasi…</p>}</form>;
}
export function CatalogCommandReview({command,ownerLabel,locked,onSubmit,onCancel}:{command:CatalogCommand;ownerLabel:string;locked:boolean;onSubmit:()=>void;onCancel:()=>void}){
 const labels={'catalog.category.save':'Simpan kategori','catalog.family.create':'Simpan kelompok produk','catalog.family.edit':'Ubah kelompok produk','catalog.sku.create':'Simpan varian baru','catalog.sku.revise':'Simpan versi kemasan','catalog.sku.replace':'Simpan SKU pengganti','catalog.sku.retire':'Hentikan SKU','catalog.sku.publish':'Simpan publikasi'};
 return <section className={common.review} aria-labelledby="catalog-review-title"><h2 id="catalog-review-title">Periksa perubahan</h2><p><strong>{labels[command.operation]}</strong> pada {ownerLabel}.</p>
 {'name' in command.body&&<p>{command.body.name}{'code' in command.body?' — '+command.body.code:''}</p>}
 {'definition' in command.body&&<><p>{'code' in command.body?<strong>{command.body.code}: </strong>:null}{command.body.definition.packageLabel} berisi <strong>{command.body.definition.baseUnitsPerPackage} {command.body.definition.baseUnitCode}</strong> per kemasan.</p><p>Minimum {command.body.definition.minimumOrderPackages} kemasan; kelipatan {command.body.definition.orderIncrementPackages} kemasan.</p>{command.body.definition.attributes.length>0&&<ul>{command.body.definition.attributes.map(item=><li key={item.key}>{item.key}: {item.value}</li>)}</ul>}</>}
 {command.operation==='catalog.sku.replace'&&<p className={styles.notice}>Identitas baru akan dibuat. SKU lama tetap aktif sampai dihentikan melalui tindakan terpisah.</p>}
 {command.operation==='catalog.sku.retire'&&<p className={styles.notice}>Seluruh publikasi SKU akan dilepas. Riwayatnya tetap ada.</p>}
 {command.operation==='catalog.sku.publish'&&<p>{command.body.assortmentIds.length} tujuan akan tetap dipilih. Seluruh tujuan lama yang tidak dipilih akan dilepas.</p>}
 <p className={common.preserve}>Alasan: {command.reason}</p><div className={common.actions}><button type="button" className={command.operation==='catalog.sku.retire'?common.danger:common.primary} disabled={locked} onClick={onSubmit}>{locked?'Menyimpan…':'Konfirmasi dan simpan'}</button><button type="button" className={common.secondary} disabled={locked} onClick={onCancel}>Kembali ke isian</button></div></section>;
}
