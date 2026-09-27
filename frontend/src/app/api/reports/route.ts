import { currentActor,errorResponse } from "@legacy/server/http";
import { loadState } from "@legacy/server/repository";
import { reportCSV } from "@legacy/server/documents";
import { allowed,today } from "@legacy/domain/selectors";
import { DomainError } from "@legacy/domain/model";
export async function GET(request:Request){try{const actor=await currentActor(request);allowed(actor,["kepala","laporan","penagihan","pimpinan","akuntansi"]);const end=new URL(request.url).searchParams.get("end")||today();if(!/^\d{4}-\d{2}-\d{2}$/.test(end))throw new DomainError("Tanggal tidak valid.");return new Response(reportCSV(await loadState(),end),{headers:{"Content-Type":"text/csv; charset=utf-8","Content-Disposition":`attachment; filename="laporan-unit-toko-${end}.csv"`,"Cache-Control":"private, no-store","X-Content-Type-Options":"nosniff"}});}catch(err){return errorResponse(err);}}
