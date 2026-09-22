import { files } from "@/lib/server/files";
import { currentActor, errorResponse, json, sameOrigin } from "@/lib/server/http";
import { execute, loadState } from "@/lib/server/repository";
import { readAttachmentUpload, saveAttachment } from "@/lib/server/attachment-upload";

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
