"use client";

import {useRef,useState,useSyncExternalStore} from "react";
import Link from "next/link";
import {ArrowLeft,ArrowRight,ClipboardList,Eye,EyeOff,LockKeyhole,Mail,Package,ReceiptText,ShieldCheck,Truck} from "lucide-react";
import {Art} from "./art";
import {Brand} from "./brand";
import {useLoginMotion} from "./login-motion";
import {Button} from "@/components/ui/button";
import {Field,FieldError,FieldGroup,FieldLabel} from "@/components/ui/field";
import {InputGroup,InputGroupAddon,InputGroupButton,InputGroupInput} from "@/components/ui/input-group";
import {safeLoginDestination, type LoginPortal} from "@/lib/domain/navigation";
import {ApiError,requestJson} from "@/lib/client/requests";
import type {Role} from "@/lib/domain/accounts";

const subscribeToHydration=()=>()=>{};
const clientReady=()=>true;
const serverReady=()=>false;

export function Login({initialError="",portal="staff"}:{initialError?:string;portal?:LoginPortal}){
 const motionRoot=useRef<HTMLElement>(null);
 useLoginMotion(motionRoot,portal);
 const customer=portal==="customer";
 const[email,setEmail]=useState(""),[password,setPassword]=useState(""),[show,setShow]=useState(false),[busy,setBusy]=useState(false),[error,setError]=useState(initialError),[credentialsInvalid,setCredentialsInvalid]=useState(false);
 const ready=useSyncExternalStore(subscribeToHydration,clientReady,serverReady);
 const pending=useRef(false);
 async function submit(e:React.FormEvent){
  e.preventDefault();if(!ready||pending.current)return;pending.current=true;setBusy(true);setError("");setCredentialsInvalid(false);
  try{
   const data=await requestJson<{user:{role:Role}}>("/api/auth/login",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({email,password,portal})});
   window.location.assign(safeLoginDestination(new URLSearchParams(window.location.search).get("next"),data.user?.role||"pic"));
  }catch(e){setCredentialsInvalid(e instanceof ApiError&&e.status===401);setError(e instanceof Error?e.message:"Koneksi bermasalah. Coba kembali.");pending.current=false;setBusy(false);}
 }
 return <main ref={motionRoot} className={`login-v13 login-portal-${portal}`}>
  <header className="login-v13-nav">
   <Brand inverse={!customer} context={customer?"Belanja kebutuhan harian":"Portal internal"}/>
   <Link href="/" className="login-v13-back"><ArrowLeft size={16} aria-hidden="true"/><span>Kembali ke beranda</span></Link>
  </header>
  <div className="login-v13-layout">
   <section className={`login-v13-intro ${customer?"login-customer-editorial":"login-staff-editorial"}`} aria-labelledby="login-portal-title">
    <div className="login-v13-story">
     <p className="login-v13-kicker" data-login-copy>{customer?"PILIHAN UNTUK HARI ANDA":"RUANG KERJA UNIT TOKO"}</p>
     <h1 id="login-portal-title" data-login-copy>{customer?<>Kebutuhan hari ini,<br/><span>beres di sini.</span></>:<>Pekerjaan tertata.<br/><span>Tim terhubung.</span></>}</h1>
     <p className="login-v13-description" data-login-copy>{customer?"Dari isi pantry sampai bekal rapat. Masuk untuk menyimpan pilihan dan mengikuti pesanan Anda.":"Satu alur kerja untuk menyiapkan pesanan, menjaga persediaan, dan menyelesaikan administrasi."}</p>
    </div>
    {customer?<figure className="login-customer-art" data-login-art>
     <div className="login-customer-photo"><Art loading="eager" fetchPriority="high" src="/images/editorial/hero-still-life-v3.png" alt="Pilihan Unit Toko: tumbler, tas kanvas, kopi, dan perlengkapan harian" sizes="(max-width: 760px) 130px, 54vw"/></div>
     <figcaption><span>Pantry · Rapat · Merchandise</span><Link href="/shop">Lihat etalase<ArrowRight size={15} aria-hidden="true"/></Link></figcaption>
    </figure>:<div className="login-staff-process">
     <p className="login-staff-process-caption">Alur operasional</p>
     <ol>
      {[
       {label:"Pesanan",detail:"Kebutuhan masuk dan ditinjau",Icon:ClipboardList},
       {label:"Persediaan",detail:"Stok diperiksa dan disiapkan",Icon:Package},
       {label:"Pengiriman",detail:"Barang dikirim dan diterima",Icon:Truck},
       {label:"Penagihan",detail:"Invoice dan pembayaran dicatat",Icon:ReceiptText},
      ].map(({label,detail,Icon},index)=><li key={label}>
       {index<3&&<span className="login-process-connector" data-login-path aria-hidden="true"/>}
       <span className="login-process-icon" data-login-step><Icon size={21} aria-hidden="true"/></span>
       <div data-login-step><strong>{label}</strong><span>{detail}</span></div>
       {index<3&&<ArrowRight className="login-process-arrow" size={16} aria-hidden="true"/>}
      </li>)}
     </ol>
    </div>}
   </section>
   <section className="login-v13-form-side" aria-labelledby="login-title">
   <div className="login-v13-panel">
    <header className="login-v13-form-header">
     <span className="login-v13-form-symbol" aria-hidden="true">{customer?<LockKeyhole size={22}/>:<ShieldCheck size={24}/>}</span>
     <p className="login-v13-form-eyebrow">{customer?"AKUN BELANJA ANDA":"AKSES SESUAI PERAN"}</p>
     <h2 id="login-title">{customer?"Masuk pelanggan":"Masuk staf & admin"}</h2>
     <p>{customer?"Gunakan akun pelanggan untuk melanjutkan belanja.":"Untuk PIC divisi, staf toko, dan administrator."}</p>
    </header>
    <form method="post" onSubmit={submit} aria-busy={busy||!ready}>
     <FieldGroup className="login-fields">
      <Field data-disabled={busy} data-invalid={credentialsInvalid}>
       <FieldLabel htmlFor="email">Email</FieldLabel>
       <InputGroup className="login-input-group" data-disabled={busy}>
        <InputGroupAddon><Mail aria-hidden="true"/></InputGroupAddon>
        <InputGroupInput id="email" name="email" type="email" autoComplete="username" placeholder={customer?"Email pelanggan":"Email akun internal"} value={email} onChange={e=>{setEmail(e.target.value);setCredentialsInvalid(false);setError("");}} aria-invalid={credentialsInvalid} aria-describedby={error?"login-error":undefined} required disabled={busy||!ready}/>
       </InputGroup>
      </Field>
      <Field data-disabled={busy} data-invalid={credentialsInvalid}>
       <FieldLabel htmlFor="password">Kata sandi</FieldLabel>
       <InputGroup className="login-input-group" data-disabled={busy}>
        <InputGroupAddon><LockKeyhole aria-hidden="true"/></InputGroupAddon>
        <InputGroupInput id="password" name="password" type={show?"text":"password"} autoComplete="current-password" placeholder="Masukkan kata sandi" value={password} onChange={e=>{setPassword(e.target.value);setCredentialsInvalid(false);setError("");}} aria-invalid={credentialsInvalid} aria-describedby={error?"login-error":undefined} required disabled={busy||!ready}/>
        <InputGroupAddon align="inline-end">
         <InputGroupButton type="button" size="icon-sm" onClick={()=>setShow(!show)} aria-label={show?"Sembunyikan kata sandi":"Tampilkan kata sandi"} aria-pressed={show} aria-controls="password">{show?<EyeOff aria-hidden="true"/>:<Eye aria-hidden="true"/>}</InputGroupButton>
        </InputGroupAddon>
       </InputGroup>
      </Field>
     </FieldGroup>
     {error&&<FieldError id="login-error" className="error-message">{error}</FieldError>}
     <Button type="submit" disabled={busy||!ready} className="login-submit">{!ready?"Menyiapkan halaman…":busy?"Memeriksa akun…":customer?"Masuk untuk belanja":"Masuk ke ruang kerja"}<ArrowRight data-icon="inline-end" aria-hidden="true"/></Button>
    </form>
    <p className="login-v13-switch">{customer?"Anda staf atau admin? ":"Ingin berbelanja? "}<Link href={customer?"/staff/login":"/customer/login"}>{customer?"Masuk staf & admin":"Masuk pelanggan"}<ArrowRight size={14} aria-hidden="true"/></Link></p>
    <div className="login-v13-help"><strong>Bantuan akun</strong><p>Hubungi administrator untuk akses akun atau bantuan kata sandi.</p></div>
   </div>
   <p className="login-v13-footnote">Demo capstone · Data simulasi<span>Studi kasus Unit Toko untuk divisi BNI</span></p>
   </section>
  </div>
 </main>;
}
