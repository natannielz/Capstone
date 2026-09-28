"use client";
import {useId} from "react";
import type {VariantDraft} from "./catalog-editor-state";
import common from "../administration/access.module.css";
import styles from "./catalog-editor.module.css";

export function CatalogVariantFields({draft,mode,disabled,onChange}:{draft:VariantDraft;mode:"create"|"revise"|"replace";disabled:boolean;onChange:(draft:VariantDraft)=>void}){
 const id=useId(),materialLocked=disabled||mode==="revise";
 const update=(key:Exclude<keyof VariantDraft,"attributes">,value:string)=>onChange({...draft,[key]:value});
 return <fieldset className={common.fields} disabled={disabled}>
  <legend className={styles.legend}>Identitas dan isi kemasan</legend>
  {mode==="revise"&&<p className={common.help}>Perubahan deskripsi dan aturan pemesanan membuat versi baru pada SKU yang sama. Untuk mengubah isi atau sifat barang, pilih Buat SKU pengganti.</p>}
  {mode==="replace"&&<p className={styles.notice}>SKU pengganti memiliki kode dan identitas baru. SKU lama beserta riwayatnya tetap ada; penggantian tidak menghentikan SKU lama secara otomatis.</p>}
  <div className={styles.twoFields}>
   <label>Kode SKU<input required maxLength={64} autoComplete="off" value={draft.code} disabled={materialLocked} onChange={event=>update("code",event.target.value)}/></label>
   <label>Identitas barang dasar<input required maxLength={128} value={draft.physicalItemKey} disabled={materialLocked} aria-describedby={id+"-physical"} onChange={event=>update("physicalItemKey",event.target.value)}/></label>
  </div>
  <p id={id+"-physical"} className={common.help}>Gunakan identitas barang dasar yang sama hanya untuk barang dengan sifat, ukuran, warna, dan bahan yang sama. Kode SKU tidak boleh digunakan kembali untuk barang lain.</p>
  <div className={styles.twoFields}>
   <label>Nama kemasan<input required maxLength={160} value={draft.packageLabel} onChange={event=>update("packageLabel",event.target.value)}/></label>
   <label>Jenis kemasan<select aria-label="Jenis kemasan" required value={draft.packageKind} disabled={materialLocked} onChange={event=>update("packageKind",event.target.value)}><option value="">Pilih jenis kemasan</option><option value="loose">Lepas</option><option value="sealed">Tersegel</option><option value="other">Lainnya</option></select></label>
  </div>
  <div className={styles.packageFacts}>
   <label>Isi unit dasar per kemasan<input required inputMode="numeric" pattern="[1-9][0-9]*" maxLength={10} value={draft.baseUnitsPerPackage} disabled={materialLocked} aria-describedby={id+"-contents"} onChange={event=>update("baseUnitsPerPackage",event.target.value)}/></label>
   <label>Kode unit dasar<input required maxLength={64} value={draft.baseUnitCode} disabled={materialLocked} onChange={event=>update("baseUnitCode",event.target.value)}/></label>
   <p id={id+"-contents"}>Isi dari spesifikasi barang yang sudah diperiksa. Nama kemasan tidak menentukan jumlah isinya.</p>
  </div>
  <fieldset className={styles.attributeFields}><legend>Sifat barang</legend>
   <p className={common.help}>Catat sifat yang membedakan barang, seperti warna, ukuran, atau bahan. Mengubah sifat memerlukan SKU pengganti.</p>
   {draft.attributes.map((attribute,index)=><div className={styles.attributeRow} key={index}>
    <label>Nama sifat {index+1}<input required maxLength={64} disabled={materialLocked} value={attribute.key} onChange={event=>onChange({...draft,attributes:draft.attributes.map((item,i)=>i===index?{...item,key:event.target.value}:item)})}/></label>
    <label>Nilai sifat {index+1}<input required maxLength={256} disabled={materialLocked} value={attribute.value} onChange={event=>onChange({...draft,attributes:draft.attributes.map((item,i)=>i===index?{...item,value:event.target.value}:item)})}/></label>
    <button type="button" className={common.secondary} disabled={materialLocked} aria-label={"Hapus sifat "+(index+1)} onClick={()=>onChange({...draft,attributes:draft.attributes.filter((_,i)=>i!==index)})}>Hapus</button>
   </div>)}
   {!draft.attributes.length&&<p className={common.help}>Belum ada sifat tambahan.</p>}
   <button type="button" className={common.secondary} disabled={materialLocked||draft.attributes.length>=32} onClick={()=>onChange({...draft,attributes:[...draft.attributes,{key:"",value:""}]})}>Tambah sifat</button>
  </fieldset>
  <div className={styles.twoFields}>
   <label>Minimum kemasan per pesanan<input required inputMode="numeric" pattern="[1-9][0-9]*" maxLength={7} value={draft.minimumOrderPackages} onChange={event=>update("minimumOrderPackages",event.target.value)}/></label>
   <label>Kelipatan jumlah kemasan<input required inputMode="numeric" pattern="[1-9][0-9]*" maxLength={7} value={draft.orderIncrementPackages} onChange={event=>update("orderIncrementPackages",event.target.value)}/></label>
  </div>
  <p className={common.help}>Minimum dan kelipatan dihitung dalam kemasan, bukan unit dasar. Keduanya wajib diisi, sampai 1.000.000 kemasan.</p>
 </fieldset>;
}
