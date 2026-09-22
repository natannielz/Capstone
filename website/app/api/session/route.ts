import { DomainError } from "@/lib/domain/model";
import { currentActor, errorResponse, json } from "@/lib/server/http";

export async function GET(request: Request) {
  try {
    return json({actor: await currentActor(request)});
  } catch (error) {
    if (error instanceof DomainError && error.status === 401) return json({actor: null});
    return errorResponse(error);
  }
}
