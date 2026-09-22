import { body,currentActor,errorResponse,json,sessionCookie } from "@/lib/server/http";
import { database,execute } from "@/lib/server/repository";
import { DomainError } from "@/lib/domain/model";
import { hashPassword,timingEqual } from "@/lib/server/security";
export async function PATCH(request:Request){try{
 const actor=await currentActor(request),input=await body(request);
 if(!input||typeof input!=="object"||Array.isArray(input))throw new DomainError("Format profil tidak valid.");
 if(Object.keys(input).some(k=>!["name","phone","position","address"].includes(k)))throw new DomainError("Hanya nama, nomor kontak, jabatan, dan alamat yang dapat diubah.",403);
 if(input.address!==undefined){if(typeof input.address!=="string"||input.address.trim().length>500)throw new DomainError("Alamat maksimal 500 karakter.");input.address=input.address.trim();}
 return json(await execute(actor,{id:crypto.randomUUID(),type:"profile.update",data:input}));
 }catch(err){return errorResponse(err);}}
export async function POST(request:Request){try{
 const actor=await currentActor(request),input=await body(request);
 if(typeof input.currentPassword!=="string"||typeof input.newPassword!=="string"||input.newPassword.length<12||input.newPassword.length>128)throw new DomainError("Gunakan kata sandi baru 12–128 karakter.");
 const db=database(),row=await db.prepare("SELECT salt,hash FROM credentials WHERE user_id=?").bind(actor.id).first<{salt:string;hash:string}>();
 if(!row||!timingEqual(await hashPassword(input.currentPassword,row.salt),row.hash))throw new DomainError("Kata sandi saat ini tidak sesuai.",400);
 const salt=crypto.randomUUID(),hash=await hashPassword(input.newPassword,salt);
 const updated=await db.batch([db.prepare("UPDATE credentials SET salt=?,hash=? WHERE user_id=? AND hash=? AND salt=? RETURNING user_id").bind(salt,hash,actor.id,row.hash,row.salt),db.prepare("DELETE FROM sessions WHERE user_id=? AND EXISTS(SELECT 1 FROM credentials WHERE user_id=? AND hash=?)").bind(actor.id,actor.id,hash)]);
 if(!updated[0].results.length)throw new DomainError("Kredensial berubah. Masuk kembali sebelum mengganti kata sandi.",409);
 return json({message:"Kata sandi diperbarui. Masuk kembali dengan kata sandi baru."},200,{"Set-Cookie":sessionCookie("",request,0)});
 }catch(err){return errorResponse(err);}}
