"use client";

import {useRef,useState,useSyncExternalStore} from "react";
import Link from "next/link";
import {ArrowLeft,ArrowRight,Eye,EyeOff,LockKeyhole,Mail} from "lucide-react";
import {Art} from "./art";
import {Brand} from "./landing";
import {Button} from "@/components/ui/button";
import {Field,FieldError,FieldGroup,FieldLabel} from "@/components/ui/field";
import {InputGroup,InputGroupAddon,InputGroupButton,InputGroupInput} from "@/components/ui/input-group";
import {safeLoginDestination} from "@/lib/domain/navigation";
import {ApiError,requestJson} from "@/lib/client/requests";
import type {Role} from "@/lib/domain/accounts";

const subscribeToHydration=()=>()=>{};
const clientReady=()=>true;
const serverReady=()=>false;

export function Login({initialError=""}:{initialError?:string}){
 const[email,setEmail]=useState(""),[password,setPassword]=useState(""),[show,setShow]=useState(false),[busy,setBusy]=useState(false),[error,setError]=useState(initialError),[credentialsInvalid,setCredentialsInvalid]=useState(false);
 const ready=useSyncExternalStore(subscribeToHydration,clientReady,serverReady);
 const pending=useRef(false);
 async function submit(e:React.FormEvent){
  e.preventDefault();if(!ready||pending.current)return;pending.current=true;setBusy(true);setError("");setCredentialsInvalid(false);
  try{
   const data=await requestJson<{user:{role:Role}}>("/api/auth/login",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({email,password})});
   window.location.assign(safeLoginDestination(new URLSearchParams(window.location.search).get("next"),data.user?.role||"pic"));
  }catch(e){setCredentialsInvalid(e instanceof ApiError&&e.status===401);setError(e instanceof Error?e.message:"Koneksi bermasalah. Coba kembali.");pending.current=false;setBusy(false);}
 }
 return <main className="login-page">
  <section className="login-visual" aria-labelledby="login-portal-title">
   <Art loading="eager" className="login-scene" src="/images/editorial/pantry-moment-v3.png" alt="Persiapan kopi dan makanan rapat di pantry kantor"/>
   <Link href="/" className="login-home"><ArrowLeft size={16} aria-hidden="true"/>Kembali ke beranda</Link>
   <div className="login-story">
    <span>UNIT TOKO</span>
    <h1 id="login-portal-title">Portal Divisi BNI</h1>
    <p>Pesanan, pengiriman, dan tagihan Unit Toko.</p>
   </div>
  </section>
  <section className="login-form-side" aria-labelledby="login-title">
   <Brand/>
   <div className="login-form-wrap">
    <header className="login-form-header">
     <h2 id="login-title">Masuk</h2>
     <p>Gunakan email dan kata sandi akun Anda.</p>
    </header>
    <form method="post" onSubmit={submit} aria-busy={busy||!ready}>
     <FieldGroup className="login-fields">
      <Field data-disabled={busy} data-invalid={credentialsInvalid}>
       <FieldLabel htmlFor="email">Email</FieldLabel>
       <InputGroup className="login-input-group" data-disabled={busy}>
        <InputGroupAddon><Mail aria-hidden="true"/></InputGroupAddon>
        <InputGroupInput id="email" name="email" type="email" autoComplete="username" placeholder="nama@unit-toko.demo" value={email} onChange={e=>{setEmail(e.target.value);setCredentialsInvalid(false);setError("");}} aria-invalid={credentialsInvalid} aria-describedby={error?"login-error":undefined} required disabled={busy||!ready}/>
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
     <Button type="submit" disabled={busy||!ready} className="login-submit">{!ready?"Menyiapkan halaman…":busy?"Memeriksa akun…":"Masuk"}<ArrowRight data-icon="inline-end" aria-hidden="true"/></Button>
    </form>
    <div className="login-help"><p><strong>Bantuan akun</strong>Hubungi administrator untuk akses akun atau bantuan kata sandi.</p></div>
   </div>
   <p className="login-footnote">Demo capstone · Data simulasi<br/>Studi kasus Unit Toko untuk divisi BNI</p>
  </section>
 </main>;
}
