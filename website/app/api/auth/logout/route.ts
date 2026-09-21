import { sameOrigin,json,errorResponse,sessionCookie } from "@/lib/server/http";
import { database } from "@/lib/server/repository";
import { digest } from "@/lib/server/security";
export async function POST(request:Request){try{sameOrigin(request);const raw=request.headers.get("cookie")?.match(/(?:^|;\s*)toko_session=([^;]+)/)?.[1];if(raw)await database().prepare("DELETE FROM sessions WHERE id=?").bind(await digest(raw)).run();return json({ok:true},200,{"Set-Cookie":sessionCookie("",request,0)});}catch(err){return errorResponse(err);}}
