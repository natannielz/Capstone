import { files } from "@/lib/server/files";
import { currentActor,errorResponse,json,sameOrigin } from "@/lib/server/http";
import { execute,loadState } from "@/lib/server/repository";
import { attachmentContext } from "@/lib/domain/selectors";
import { DomainError } from "@/lib/domain/model";
export async function POST(request:Request){let key:string|undefined;const bucket=files;try{
 sameOrigin(request);const actor=await currentActor(request);if(!bucket)throw new Error("Penyimpanan berkas belum tersedia.");
 const max=4*1024*1024+20000;if(Number(request.headers.get("content-length")||0)>max)throw new DomainError("Berkas maksimal 4 MB.",413);
 const reader=request.body?.getReader();if(!reader)throw new DomainError("Pilih berkas.");const chunks:Uint8Array[]=[];let size=0;while(true){const next=await reader.read();if(next.done)break;size+=next.value.length;if(size>max){await reader.cancel();throw new DomainError("Berkas maksimal 4 MB.",413);}chunks.push(next.value);}
 const bytes=new Uint8Array(size);let offset=0;for(const c of chunks){bytes.set(c,offset);offset+=c.length;}const form=await new Response(bytes,{headers:{"content-type":request.headers.get("content-type")||""}}).formData(),file=form.get("file"),scope=String(form.get("scope")||""),targetId=String(form.get("targetId")||"");
 if(!file||typeof file==="string"||file.size<1||file.size>4*1024*1024)throw new DomainError("Pilih berkas PNG, JPG, atau PDF maksimal 4 MB.");
 const state=await loadState();attachmentContext(state,actor,scope,targetId);const latestClosed=state.periods.filter(p=>p.status==="closed").map(p=>p.id).sort().at(-1),now=new Date().toLocaleDateString("en-CA",{timeZone:"Asia/Jakarta"});let date=now;if(latestClosed&&latestClosed>=now.slice(0,7)){const [y,m]=latestClosed.split("-").map(Number);date=new Date(Date.UTC(y,m,1)).toISOString().slice(0,10);}const content=await file.arrayBuffer(),head=new Uint8Array(content).slice(0,8);const mime=head[0]===0xff&&head[1]===0xd8&&head[2]===0xff?"image/jpeg":head.join(",")==="137,80,78,71,13,10,26,10"?"image/png":new TextDecoder().decode(head.slice(0,5))==="%PDF-"?"application/pdf":"";
 if(!mime)throw new DomainError("Isi berkas harus PNG, JPG, atau PDF.");key=crypto.randomUUID();await bucket.put(key,content,{httpMetadata:{contentType:mime}});const name=file.name.replace(/[\x00-\x1f\\/]/g,"_").slice(0,180)||"Bukti simulasi";
 const result=await execute(actor,{id:crypto.randomUUID(),type:"attachment.add",date,data:{attachmentId:key,targetId,scope,name,mime,size:file.size}});return json(result,201);
 }catch(err){if(key&&bucket)await bucket.delete(key).catch(()=>{});return errorResponse(err);}}
