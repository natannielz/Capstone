// Historical handler retained for migration/security regression tests; not a Next.js route.
import { files } from "../../server/files";
import { currentActor, errorResponse, json, sameOrigin } from "../../server/http";
import { execute, loadState } from "../../server/repository";
import { readAttachmentUpload, saveAttachment } from "../../server/attachment-upload";

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
