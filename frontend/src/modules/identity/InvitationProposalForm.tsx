"use client";

import { useMemo, useState, type FormEvent } from "react";
import type { components } from "../../api/generated/schema";
import { scopeKindLabels } from "../organizations/access-scope";
import { invitationDates, invitationRole, invitationScopeOptions, invitationScopeKinds } from "./invitation-scope";
import styles from "../administration/access.module.css";

type Schemas = components["schemas"];
type Proposal = Schemas["IdentityInvitationProposalDto"];

export function InvitationProposalForm({current,catalog,locked,onSubmit,labels}: {
  current:Schemas["CurrentAccessDto"]; catalog:Schemas["AccessCatalogDto"]; locked:boolean;
  onSubmit:(body:Proposal)=>Promise<void>; labels?:Readonly<Record<string,string>>;
}) {
  const [email,setEmail]=useState("");
  const [kind,setKind]=useState<Schemas["GrantScopeKind"]|"">("");
  const [scopeKey,setScopeKey]=useState("");
  const [templateId,setTemplateId]=useState("");
  const [packs,setPacks]=useState<string[]>([]);
  const [inviteExpiry,setInviteExpiry]=useState("");
  const [accessExpiry,setAccessExpiry]=useState("");
  const [reason,setReason]=useState("");
  const [review,setReview]=useState<string|null>(null);
  const [busy,setBusy]=useState(false);
  const choices=useMemo(()=>invitationScopeOptions(current,labels),[current,labels]);
  const scope=choices.find(choice=>choice.key===scopeKey&&choice.scope.kind===kind)?.scope;
  const dates=invitationDates(inviteExpiry,accessExpiry);
  const role=scope&&templateId?invitationRole(catalog,scope,templateId,packs):null;
  const normalizedEmail=email.trim().toLowerCase();
  const validEmail=/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(normalizedEmail)&&normalizedEmail.length<=254;
  const candidate:Proposal|null=scope&&dates&&validEmail&&reason.trim()&&(!templateId||role)?{
    email:normalizedEmail,scope,expiresAt:dates.expiresAt,accessExpiresAt:dates.accessExpiresAt,
    reason:reason.trim(),role:role??null,
  }:null;
  const content=candidate?JSON.stringify(candidate):null;
  const confirmed=Boolean(content&&review===content);
  const disabled=locked||busy||current.status!=="active";
  const change=(apply:()=>void)=>{setReview(null);apply();};
  const reset=()=>{setEmail("");setKind("");setScopeKey("");setTemplateId("");setPacks([]);setInviteExpiry("");setAccessExpiry("");setReason("");setReview(null);};
  const submit=async(event:FormEvent<HTMLFormElement>)=>{
    event.preventDefault();
    if(disabled||!candidate||!confirmed)return;
    setBusy(true);
    try{await onSubmit(candidate);reset();}catch{/* The parent owns request errors and recovery; preserve the reviewed draft. */}finally{setBusy(false);}
  };
  const dateLabel=(value:string)=>new Intl.DateTimeFormat("id-ID",{dateStyle:"medium",timeStyle:"short",timeZone:"Asia/Jakarta"}).format(new Date(value));

  return <form className={styles.form} onSubmit={event=>{void submit(event);}} aria-label="Usulkan undangan akun">
    <h2>Usulkan undangan akun</h2>
    <p className={styles.help}>Pilih lingkup dan kewenangan yang akan diperiksa sebelum undangan disetujui. Alamat email harus sudah disepakati dengan penerima.</p>
    <fieldset className={styles.fields} disabled={disabled}>
      <label>Alamat email penerima
        <input type="email" autoComplete="email" required maxLength={254} value={email} onChange={event=>change(()=>setEmail(event.target.value))}/>
      </label>
      <div className={styles.twoFields}>
        <label>Jenis lingkup
          <select required value={kind} onChange={event=>change(()=>{setKind(event.target.value as Schemas["GrantScopeKind"]|"");setScopeKey("");setTemplateId("");setPacks([]);})}>
            <option value="">Pilih jenis lingkup</option>
            {invitationScopeKinds.filter(value=>choices.some(choice=>choice.scope.kind===value)).map(value=><option key={value} value={value}>{scopeKindLabels[value]}</option>)}
          </select>
        </label>
        <label>Organisasi dan unit yang dituju
          <select required value={scopeKey} onChange={event=>change(()=>{setScopeKey(event.target.value);setTemplateId("");setPacks([]);})}>
            <option value="">Pilih jalur organisasi</option>
            {choices.filter(choice=>choice.scope.kind===kind).map(choice=><option key={choice.key} value={choice.key}>{choice.label}</option>)}
          </select>
        </label>
      </div>
      <fieldset className={styles.choiceGroup}>
        <legend>Hak awal</legend>
        <label className={styles.checkbox}><input type="radio" name="invite-role" checked={!templateId} onChange={()=>change(()=>{setTemplateId("");setPacks([]);})}/> Keanggotaan saja, tanpa peran</label>
        {scope&&catalog.roles.filter(item=>invitationRole(catalog,scope,item.id,[])!==null).map(item=><label key={item.id} className={styles.checkbox}>
          <input type="radio" name="invite-role" checked={templateId===item.id} onChange={()=>change(()=>{setTemplateId(item.id);setPacks([]);})}/>{item.label}
        </label>)}
      </fieldset>
      {scope&&templateId==="finance"&&<fieldset className={styles.choiceGroup}>
        <legend>Paket keuangan tambahan</legend>
        {catalog.financePacks.filter(pack=>invitationRole(catalog,scope,templateId,[pack.id])!==null).map(pack=><label key={pack.id} className={styles.checkbox}>
          <input type="checkbox" checked={packs.includes(pack.id)} onChange={event=>change(()=>setPacks(event.target.checked?[...packs,pack.id]:packs.filter(id=>id!==pack.id)))}/>{pack.label}
        </label>)}
        <small>Hanya paket yang dipilih akan diminta. Persetujuan tetap diperlukan.</small>
      </fieldset>}
      <div className={styles.twoFields}>
        <label>Undangan berakhir (waktu Jakarta, paling lama 7 hari)
          <input type="datetime-local" required value={inviteExpiry} onChange={event=>change(()=>setInviteExpiry(event.target.value))}/>
        </label>
        <label>Akses berakhir (opsional, waktu Jakarta)
          <input type="datetime-local" value={accessExpiry} onChange={event=>change(()=>setAccessExpiry(event.target.value))}/>
        </label>
      </div>
      <label>Alasan pengundangan
        <textarea aria-label="Alasan pengundangan" required maxLength={1000} rows={3} value={reason} onChange={event=>change(()=>setReason(event.target.value))}/>
      </label>
      <button type="button" className={styles.secondary} disabled={!candidate} onClick={()=>setReview(content)}>Tinjau usulan</button>
    </fieldset>
    {confirmed&&candidate&&<section className={styles.review} aria-label="Tinjauan undangan">
      <h3>Periksa sebelum mengirim</h3>
      <dl className={styles.facts}>
        <div><dt>Email</dt><dd>{candidate.email}</dd></div>
        <div><dt>Lingkup</dt><dd>{choices.find(choice=>choice.key===scopeKey)?.label}</dd></div>
        <div><dt>Hak awal</dt><dd>{templateId?catalog.roles.find(item=>item.id===templateId)?.label:"Keanggotaan saja"}</dd></div>
        <div><dt>Paket keuangan</dt><dd>{packs.length?packs.map(id=>catalog.financePacks.find(pack=>pack.id===id)?.label).join(", "):"Tidak ada"}</dd></div>
        <div><dt>Izin yang diminta</dt><dd>{templateId?[...new Set([
          ...(catalog.roles.find(item=>item.id===templateId)?.permissions??[]),
          ...packs.flatMap(id=>catalog.financePacks.find(pack=>pack.id===id)?.permissions??[]),
        ])].map(id=>catalog.permissions.find(permission=>permission.id===id)?.label??id).join(", "):"Tidak ada"}</dd></div>
        <div><dt>Undangan berakhir</dt><dd>{dateLabel(candidate.expiresAt)} WIB</dd></div>
        <div><dt>Akses berakhir</dt><dd>{candidate.accessExpiresAt?`${dateLabel(candidate.accessExpiresAt)} WIB`:"Tidak ditetapkan"}</dd></div>
        <div><dt>Alasan</dt><dd className={styles.preserve}>{candidate.reason}</dd></div>
      </dl>
      <button type="submit" className={styles.primary} disabled={disabled}>Kirim usulan undangan</button>
    </section>}
  </form>;
}
