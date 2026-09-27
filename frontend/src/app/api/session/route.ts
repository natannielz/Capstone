import { DomainError } from "@legacy/domain/model";
import { currentActor, errorResponse, json } from "@legacy/server/http";

export async function GET(request: Request) {
  try {
    return json({actor: await currentActor(request)});
  } catch (error) {
    if (error instanceof DomainError && error.status === 401) return json({actor: null});
    return errorResponse(error);
  }
}
