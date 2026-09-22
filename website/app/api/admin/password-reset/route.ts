import {body, currentActor, errorResponse, json} from "@/lib/server/http";
import {database} from "@/lib/server/repository";
import {digest, hashPassword, timingEqual} from "@/lib/server/security";
import {DomainError} from "@/lib/domain/model";
import {today} from "@/lib/domain/selectors";

/** Manual account recovery; never exposes passwords in results, commands, or audits. */
export async function POST(request: Request) {
  try {
    const actor = await currentActor(request);
    if (actor.role !== "admin") throw new DomainError("Hanya administrator yang dapat mereset kata sandi akun.", 403);
    const input = await body(request);
    if (!input || typeof input !== "object" || Array.isArray(input)
      || Object.keys(input).some(key => !["id", "targetId", "currentPassword", "newPassword", "reason"].includes(key))) {
      throw new DomainError("Format permintaan reset tidak valid.");
    }
    const {id, targetId, currentPassword, newPassword} = input;
    if (typeof id !== "string" || !/^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i.test(id)
      || typeof targetId !== "string" || !targetId || targetId.length > 100) throw new DomainError("Pilih akun yang akan direset.");
    if (targetId === actor.id) throw new DomainError("Ganti kata sandi akun Anda sendiri melalui Profil saya.", 403);
    if (typeof currentPassword !== "string" || currentPassword.length > 128
      || typeof newPassword !== "string" || newPassword.length < 12 || newPassword.length > 128) {
      throw new DomainError("Kata sandi baru harus terdiri dari 12–128 karakter.");
    }
    if (typeof input.reason !== "string" || !input.reason.trim() || input.reason.trim().length > 500) {
      throw new DomainError("Isi alasan reset, maksimal 500 karakter.");
    }
    const reason = input.reason.trim(), db = database(), now = Date.now();
    const attemptKey = await digest(`admin-password-reset:${actor.id}`);
    await db.prepare("INSERT INTO login_attempts(key,attempts,expires_at) VALUES(?,1,?) ON CONFLICT(key) DO UPDATE SET attempts=CASE WHEN expires_at<? THEN 1 ELSE attempts+1 END,expires_at=CASE WHEN expires_at<? THEN excluded.expires_at ELSE expires_at END")
      .bind(attemptKey, now + 15 * 60 * 1000, now, now).run();
    const attempts = await db.prepare("SELECT attempts FROM login_attempts WHERE key=?").bind(attemptKey).first<{attempts: number}>();
    if ((attempts?.attempts || 0) > 8) throw new DomainError("Terlalu banyak percobaan verifikasi. Coba lagi dalam 15 menit.", 429);
    const adminCredential = await db.prepare("SELECT salt,hash FROM credentials WHERE user_id=?").bind(actor.id).first<{salt: string; hash: string}>();
    if (!adminCredential || !timingEqual(await hashPassword(currentPassword, adminCredential.salt), adminCredential.hash)) {
      throw new DomainError("Kata sandi administrator tidak sesuai.", 400);
    }
    await db.prepare("DELETE FROM login_attempts WHERE key=?").bind(attemptKey).run();
    const commandId = `admin-password-reset:${id}`;
    // Keep retries deterministic without storing a fast password verifier in the
    // command log. Password guesses must retain the credential work factor.
    const passwordProof = await hashPassword(newPassword, `${actor.id}:${commandId}`);
    const fingerprint = await digest(JSON.stringify({id, targetId, passwordProof, reason}));
    const completed = async () => {
      const existing = await db.prepare("SELECT actor_id,fingerprint,result FROM commands WHERE id=?").bind(commandId)
        .first<{actor_id: string; fingerprint: string; result: string}>();
      if (!existing) return null;
      if (existing.actor_id !== actor.id || existing.fingerprint !== fingerprint) throw new DomainError("Identitas reset telah digunakan untuk isian berbeda.", 409);
      return JSON.parse(existing.result) as {message: string};
    };
    const previous = await completed();
    if (previous) return json(previous);
    const target = await db.prepare("SELECT c.salt,c.hash,e.email FROM credentials c JOIN user_emails e ON e.user_id=c.user_id WHERE c.user_id=?")
      .bind(targetId).first<{salt: string; hash: string; email: string}>();
    if (!target) throw new DomainError("Akun tidak ditemukan.", 404);
    const version = await db.prepare("SELECT version FROM system_version WHERE id='global'").first<{version: number}>();
    if (!version) throw new DomainError("Data akun belum tersedia.", 503);
    const rawSession = request.headers.get("cookie")?.match(/(?:^|;\s*)toko_session=([^;]+)/)?.[1] || "";
    const sessionId = await digest(rawSession), salt = crypto.randomUUID(), hash = await hashPassword(newPassword, salt);
    const audit = {id: crypto.randomUUID(), actorId: actor.id, action: "admin.password.reset", targetId,
      at: new Date().toISOString(), date: today(), description: `Kata sandi akun direset oleh administrator. Alasan: ${reason}`};
    const result = {message: "Kata sandi akun diperbarui dan seluruh sesinya diakhiri. Sampaikan kata sandi baru secara pribadi kepada pemilik akun."};
    try {
      await db.batch([
        // The guard, credential replacement, session revocation, audit and revision
        // share one transaction. Re-check authority and both credential versions.
        db.prepare("INSERT INTO mutation_guards(id,valid) VALUES(?,CASE WHEN (SELECT version FROM system_version WHERE id='global')=? AND EXISTS(SELECT 1 FROM users u JOIN credentials c ON c.user_id=u.id JOIN sessions s ON s.user_id=u.id WHERE u.id=? AND u.role='admin' AND json_extract(u.payload,'$.role')='admin' AND json_extract(u.payload,'$.active')=1 AND c.hash=? AND c.salt=? AND s.id=? AND s.expires_at>?) AND EXISTS(SELECT 1 FROM credentials WHERE user_id=? AND hash=? AND salt=?) THEN 1 ELSE 0 END)")
          .bind(commandId, version.version, actor.id, adminCredential.hash, adminCredential.salt, sessionId, Date.now(), targetId, target.hash, target.salt),
        db.prepare("UPDATE credentials SET salt=?,hash=? WHERE user_id=?").bind(salt, hash, targetId),
        db.prepare("DELETE FROM sessions WHERE user_id=?").bind(targetId),
        db.prepare("DELETE FROM login_attempts WHERE key=?").bind(await digest(target.email.trim().toLowerCase())),
        db.prepare("INSERT INTO audits(id,payload,actor_id) VALUES(?,?,?)").bind(audit.id, JSON.stringify(audit), actor.id),
        db.prepare("UPDATE system_version SET version=version+1 WHERE id='global'"),
        db.prepare("INSERT INTO commands(id,actor_id,fingerprint,result) VALUES(?,?,?,?)").bind(commandId, actor.id, fingerprint, JSON.stringify(result)),
        db.prepare("DELETE FROM mutation_guards WHERE id=?").bind(commandId),
      ]);
    } catch (error) {
      if (/concurrent_write_guard|mutation_guards|UNIQUE constraint failed: commands/.test(String(error))) {
        const freshActor = await currentActor(request);
        if (freshActor.role !== "admin") throw new DomainError("Hak akses administrator telah berubah.", 403);
        const saved = await completed();
        if (saved) return json(saved);
        throw new DomainError("Data atau kredensial akun berubah. Muat ulang daftar akun sebelum mencoba kembali.", 409);
      }
      throw error;
    }
    return json(result);
  } catch (error) { return errorResponse(error); }
}
