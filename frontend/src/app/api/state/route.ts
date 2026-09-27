import { currentActor,json,errorResponse } from "@legacy/server/http";
import { loadState } from "@legacy/server/repository";
import { scopeState } from "@legacy/domain/selectors";
export async function GET(request:Request){try{const actor=await currentActor(request);return json({actor,state:scopeState(await loadState(),actor)});}catch(err){return errorResponse(err);}}
