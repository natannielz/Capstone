import { body,currentActor,json,errorResponse } from "@/lib/server/http";
import { execute } from "@/lib/server/repository";
import { DomainError } from "@/lib/domain/model";
export async function POST(request:Request){try{const actor=await currentActor(request),input=await body(request);if(typeof input.id!=="string"||input.id.length>100||input.id.length<10||typeof input.type!=="string"||!input.data||typeof input.data!=="object"||Array.isArray(input.data))throw new DomainError("Format tindakan tidak valid.");if((input.type.startsWith("attachment.")||input.type==="profile.avatar"))throw new DomainError("Gunakan unggah lampiran.",403);return json(await execute(actor,input));}catch(err){return errorResponse(err);}}
