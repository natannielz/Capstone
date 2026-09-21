import { currentActor,json,errorResponse } from "@/lib/server/http";
import { loadState } from "@/lib/server/repository";
import { scopeState } from "@/lib/domain/selectors";
export async function GET(request:Request){try{const actor=await currentActor(request);return json({actor,state:scopeState(await loadState(),actor)});}catch(err){return errorResponse(err);}}
