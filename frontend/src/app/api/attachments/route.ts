import { files } from "@legacy/server/files";
import { currentActor, errorResponse, json, sameOrigin } from "@legacy/server/http";
import { execute, loadState } from "@legacy/server/repository";
import { readAttachmentUpload, saveAttachment } from "@legacy/server/attachment-upload";

export async function POST(request: Request) {
  try {
    sameOrigin(request);
    const actor = await currentActor(request);
    const upload = await readAttachmentUpload(request);
    return json(await saveAttachment(actor, upload, {files, execute, loadState}), 201);
  } catch (error) {
    return errorResponse(error);
  }
}
