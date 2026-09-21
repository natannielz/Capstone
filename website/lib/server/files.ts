import { put, get, del } from "@vercel/blob";
import { mkdir, writeFile, readFile, unlink } from "node:fs/promises";
import { join } from "node:path";
function path(id:string){if(!/^[\w-]+$/.test(id))throw new Error("Invalid file key");return join(process.cwd(),".data","files",id);}
export const files={
  async put(id:string,data:ArrayBuffer,options:{httpMetadata:{contentType:string}}){
    if(process.env.VERCEL||process.env.BLOB_READ_WRITE_TOKEN){await put(id,Buffer.from(data),{access:"private",addRandomSuffix:false,contentType:options.httpMetadata.contentType});return;}
    await mkdir(join(process.cwd(),".data","files"),{recursive:true});await writeFile(path(id),Buffer.from(data));
  },
  async get(id:string):Promise<{body:BodyInit}|null>{
    if(process.env.VERCEL||process.env.BLOB_READ_WRITE_TOKEN){const file=await get(id,{access:"private",useCache:false});return file?.statusCode===200?{body:file.stream}:null;}
    try{return {body:new Uint8Array(await readFile(path(id)))};}catch{return null;}
  },
  async delete(id:string){if(process.env.VERCEL||process.env.BLOB_READ_WRITE_TOKEN){await del(id);return;}await unlink(path(id)).catch(()=>{});}
};
