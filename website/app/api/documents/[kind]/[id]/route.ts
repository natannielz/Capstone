import { currentActor,errorResponse } from "@/lib/server/http";
import { loadState } from "@/lib/server/repository";
import { documentHTML } from "@/lib/server/documents";
export async function GET(request:Request,{params}:{params:Promise<{kind:string;id:string}>}){try{const actor=await currentActor(request),{kind,id}=await params;return new Response(documentHTML(await loadState(),actor,kind,id),{headers:{"Content-Type":"text/html; charset=utf-8","Cache-Control":"private, no-store","X-Content-Type-Options":"nosniff","Content-Security-Policy":"default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; frame-ancestors 'self'; base-uri 'none'"}});}catch(err){return errorResponse(err);}}
