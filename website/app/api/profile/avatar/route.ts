import { currentActor,errorResponse,json,sameOrigin } from "@/lib/server/http";
import { execute } from "@/lib/server/repository";
import { files } from "@/lib/server/files";
import { DomainError } from "@/lib/domain/model";
export async function POST(request:Request){let key:string|undefined;try{
 sameOrigin(request);const actor=await currentActor(request),limit=2*1024*1024;
 const reader=request.body?.getReader();if(!reader)throw new DomainError("Pilih foto profil.");
 const chunks:Uint8Array[]=[];let size=0;while(true){const next=await reader.read();if(next.done)break;size+=next.value.length;if(size>limit+20000){await reader.cancel();throw new DomainError("Foto maksimal 2 MB.",413);}chunks.push(next.value);}
 const bytes=new Uint8Array(size);let offset=0;for(const chunk of chunks){bytes.set(chunk,offset);offset+=chunk.length;}
 const form=await new Response(bytes,{headers:{"content-type":request.headers.get("content-type")||""}}).formData(),file=form.get("file");
 if(!file||typeof file==="string"||file.size>limit)throw new DomainError("Pilih foto PNG atau JPG maksimal 2 MB.");
 const content=await file.arrayBuffer(),head=new Uint8Array(content).slice(0,8),mime=head.join(",")==="137,80,78,71,13,10,26,10"?"image/png":head[0]===255&&head[1]===216&&head[2]===255?"image/jpeg":"";
 if(!mime)throw new DomainError("Format foto harus PNG atau JPG.");
 key="avatar-"+crypto.randomUUID();await files.put(key,content,{httpMetadata:{contentType:mime}});
 await execute(actor,{id:crypto.randomUUID(),type:"profile.avatar",data:{key}});
 return json({message:"Foto profil diperbarui."});
 }catch(error){if(key)await files.delete(key).catch(()=>{});return errorResponse(error);}}
export async function GET(request:Request){try{
 const actor=await currentActor(request),key=actor.avatar?.split("?id=")[1];
 if(!key||!/^avatar-[\w-]+$/.test(key))throw new DomainError("Foto belum diunggah.",404);
 const file=await files.get(key);if(!file)throw new DomainError("Foto tidak ditemukan.",404);
 const bytes=await new Response(file.body).arrayBuffer(),head=new Uint8Array(bytes);
 return new Response(bytes,{headers:{"Cache-Control":"private, no-store","X-Content-Type-Options":"nosniff","Content-Type":head[0]===255?"image/jpeg":"image/png","Content-Security-Policy":"default-src 'none'; sandbox"}});
 }catch(error){return errorResponse(error);}}
