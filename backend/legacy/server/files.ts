import { put, get, del } from "@vercel/blob";
import { mkdir, open, link, readFile, unlink } from "node:fs/promises";
import { join } from "node:path";
function path(id:string){if(!/^[\w-]+$/.test(id))throw new Error("Invalid file key");return join(process.cwd(),".data","files",id);}
export const files={
  async put(id:string,data:ArrayBuffer,options:{httpMetadata:{contentType:string}}){
    if(process.env.VERCEL||process.env.BLOB_READ_WRITE_TOKEN){await put(id,Buffer.from(data),{access:"private",addRandomSuffix:false,allowOverwrite:false,contentType:options.httpMetadata.contentType});return;}
    const destination=path(id),directory=join(process.cwd(),".data","files");
    await mkdir(directory,{recursive:true});
    const temporary=join(directory,`.${id}-${crypto.randomUUID()}.tmp`),handle=await open(temporary,"wx");
    try{
      await handle.writeFile(Buffer.from(data));await handle.close();
      // Publishing a fully written inode avoids partial readers and, like Blob,
      // rejects a competing existing key instead of truncating its content.
      await link(temporary,destination);
    }finally{
      await handle.close().catch(()=>{});
      // Only this request's temporary file is removed, never the canonical key.
      await unlink(temporary).catch(()=>{});
    }
  },
  async get(id:string):Promise<{body:BodyInit}|null>{
    if(process.env.VERCEL||process.env.BLOB_READ_WRITE_TOKEN){const file=await get(id,{access:"private",useCache:false});return file?.statusCode===200?{body:file.stream}:null;}
    try{return {body:new Uint8Array(await readFile(path(id)))};}catch{return null;}
  },
  async delete(id:string){if(process.env.VERCEL||process.env.BLOB_READ_WRITE_TOKEN){await del(id);return;}await unlink(path(id)).catch(()=>{});}
};
