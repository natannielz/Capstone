import { files } from "@/lib/server/files";
import { currentActor,errorResponse } from "@/lib/server/http";
import { loadState } from "@/lib/server/repository";
import { canReadAttachment,get } from "@/lib/domain/selectors";
import { DomainError } from "@/lib/domain/model";
export async function GET(request:Request,{params}:{params:Promise<{id:string}>}){try{const actor=await currentActor(request),{id}=await params,s=await loadState();if(!canReadAttachment(s,actor,id))throw new DomainError("Lampiran tidak ditemukan atau tidak dapat diakses.",404);const meta=get(s.attachments,id),bucket=files,file=await bucket?.get(id);if(!file)throw new DomainError("Berkas tidak ditemukan.",404);return new Response(file.body,{headers:{"Content-Type":meta.mime,"Content-Disposition":`attachment; filename*=UTF-8''${encodeURIComponent(meta.name)}`,"Cache-Control":"private, no-store","X-Content-Type-Options":"nosniff","Content-Security-Policy":"default-src 'none'; sandbox"}});}catch(err){return errorResponse(err);}}
