// Historical handler retained for migration/security regression tests; not a Next.js route.
import { body,currentActor,errorResponse,json,sessionCookie } from "../../server/http";
import { database } from "../../server/repository";
import { DomainError } from "../../domain/model";
import { hashPassword,timingEqual } from "../../server/security";
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
