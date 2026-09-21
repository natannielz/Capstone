import { body,errorResponse,json,sessionCookie } from "@/lib/server/http";
import { database,ensureSeed } from "@/lib/server/repository";
import { digest,hashPassword,timingEqual } from "@/lib/server/security";
import { DomainError } from "@/lib/domain/model";
export async function POST(request:Request){try{
 const input=await body(request);
 if(typeof input.email!=="string"||typeof input.password!=="string"||input.email.length>180||input.password.length>128)throw new DomainError("Email atau kata sandi tidak sesuai.",401);
 await ensureSeed();const db=database(),email=input.email.trim().toLowerCase(),key=await digest(email),now=Date.now();
 await db.prepare("INSERT INTO login_attempts(key,attempts,expires_at) VALUES(?,1,?) ON CONFLICT(key) DO UPDATE SET attempts=CASE WHEN expires_at<? THEN 1 ELSE attempts+1 END,expires_at=CASE WHEN expires_at<? THEN excluded.expires_at ELSE expires_at END").bind(key,now+15*60*1000,now,now).run();
 const attempt=await db.prepare("SELECT attempts FROM login_attempts WHERE key=?").bind(key).first<{attempts:number}>();
 if((attempt?.attempts||0)>8)throw new DomainError("Terlalu banyak percobaan. Silakan coba lagi dalam 15 menit.",429);
 const row=await db.prepare("SELECT c.user_id,c.salt,c.hash,u.payload FROM user_emails e JOIN credentials c ON c.user_id=e.user_id JOIN users u ON u.id=c.user_id WHERE e.email=?").bind(email).first<{user_id:string;salt:string;hash:string;payload:string}>();
 const candidate=await hashPassword(input.password,row?.salt||"unknown-account-constant-salt");
 if(!row||!timingEqual(candidate,row.hash)||!JSON.parse(row.payload).active)throw new DomainError("Email atau kata sandi tidak sesuai.",401);
 const raw=crypto.randomUUID()+crypto.randomUUID();await db.batch([db.prepare("DELETE FROM login_attempts WHERE key=?").bind(key),db.prepare("DELETE FROM sessions WHERE expires_at<?").bind(now),db.prepare("INSERT INTO sessions(id,user_id,expires_at) SELECT ?,user_id,? FROM credentials WHERE user_id=? AND hash=? AND salt=?").bind(await digest(raw),now+8*60*60*1000,row.user_id,row.hash,row.salt)]);
 if(!await db.prepare("SELECT id FROM sessions WHERE id=?").bind(await digest(raw)).first())throw new DomainError("Kredensial berubah. Silakan masuk kembali.",401);
 return json({user:JSON.parse(row.payload)},200,{"Set-Cookie":sessionCookie(raw,request)});
 }catch(err){return errorResponse(err);}}
