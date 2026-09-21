import { createClient } from "@libsql/client";
import { drizzle } from "drizzle-orm/libsql";
import * as schema from "./schema";
export function getDb(){return drizzle(createClient({url:process.env.TURSO_DATABASE_URL||"file:.data/unit-toko.db",authToken:process.env.TURSO_AUTH_TOKEN}),{schema});}
