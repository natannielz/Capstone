"use client";

import {useEffect, useRef, useState, type FormEvent} from "react";
import {toast} from "sonner";
import type {Actor} from "@/lib/domain/accounts";
import {ApiError, requestJson} from "@/lib/client/requests";
import {Alert, AlertDescription} from "./ui/alert";
import {Button} from "./ui/button";
import {Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle} from "./ui/dialog";
import {Field, FieldDescription, FieldError, FieldGroup, FieldLabel} from "./ui/field";
import {Input} from "./ui/input";
import {Textarea} from "./ui/textarea";

type Values = {currentPassword: string; newPassword: string; confirm: string; reason: string};
type ResetRequest = {id: string; targetId: string; currentPassword: string; newPassword: string; reason: string};
const EMPTY_VALUES: Values = {currentPassword: "", newPassword: "", confirm: "", reason: ""};
const fields = ["currentPassword", "newPassword", "confirm", "reason"] as const;

export function AdminPasswordReset({target, close, refresh}: {target: Actor; close: () => void; refresh: () => Promise<void>}) {
  const [values, setValues] = useState<Values>(EMPTY_VALUES);
  const [errors, setErrors] = useState<Partial<Record<keyof Values, string>>>({});
  const [error, setError] = useState("");
  const [requestId, setRequestId] = useState(() => crypto.randomUUID());
  const [retry, setRetry] = useState<ResetRequest | null>(null);
  const [busy, setBusy] = useState(false);
  const pending = useRef(false), errorRef = useRef<HTMLDivElement>(null);
  const locked = busy || Boolean(retry);
  useEffect(() => {if (error) errorRef.current?.focus();}, [error]);

  function clearAndClose() {
    if (pending.current) return;
    setValues(EMPTY_VALUES); setRetry(null); setError(""); setErrors({}); close();
  }

  function edit(field: keyof Values, value: string) {
    if (pending.current || retry) return;
    setValues(current => ({...current, [field]: value}));
    setErrors(current => ({...current, [field]: undefined}));
    setError(""); setRequestId(crypto.randomUUID());
  }

  async function submit(event: FormEvent) {
    event.preventDefault(); if (pending.current) return;
    const nextErrors: typeof errors = {};
    if (!values.currentPassword) nextErrors.currentPassword = "Masukkan kata sandi administrator yang sedang digunakan.";
    if (values.newPassword.length < 12 || values.newPassword.length > 128) nextErrors.newPassword = "Gunakan 12–128 karakter.";
    if (values.confirm !== values.newPassword || !values.confirm) nextErrors.confirm = "Ulangi kata sandi baru dengan nilai yang sama.";
    if (!values.reason.trim()) nextErrors.reason = "Isi alasan pengaturan ulang untuk jejak aktivitas.";
    setErrors(nextErrors); setError("");
    const invalid = fields.find(field => nextErrors[field]);
    if (invalid) {document.getElementById(`admin-reset-${invalid}`)?.focus(); return;}
    const payload = retry || {id: requestId, targetId: target.id, currentPassword: values.currentPassword, newPassword: values.newPassword, reason: values.reason.trim()};
    pending.current = true; setBusy(true); setRetry(payload);
    try {
      const result = await requestJson<{message: string}>("/api/admin/password-reset", {method: "POST", headers: {"Content-Type": "application/json"}, body: JSON.stringify(payload)});
      if (!result || typeof result.message !== "string") throw new ApiError("Hasil pengaturan ulang belum dapat dipastikan. Periksa kembali permintaan yang sama.", 200);
      setValues(EMPTY_VALUES); setRetry(null); pending.current = false; close();
      toast.success(result.message);
      void refresh().catch(() => toast.error("Kata sandi sudah diperbarui. Daftar akun belum dapat dimuat ulang; gunakan tombol Muat ulang data."));
    } catch (cause) {
      if (cause instanceof ApiError && cause.status >= 400 && cause.status < 500) setRetry(null);
      setError(cause instanceof Error ? cause.message : "Pengaturan ulang belum berhasil. Coba kembali.");
    } finally {pending.current = false; setBusy(false);}
  }

  return <Dialog open onOpenChange={open => {if (!open) clearAndClose();}}><DialogContent className="action-dialog sm:max-w-xl" showCloseButton={!busy} onEscapeKeyDown={event => {if (pending.current) event.preventDefault();}} onInteractOutside={event => {if (pending.current) event.preventDefault();}}>
    <DialogHeader><DialogTitle>Atur ulang kata sandi</DialogTitle><DialogDescription>Kata sandi baru berlaku untuk {target.name} ({target.email || target.id}). Semua sesi akun tersebut akan diakhiri. Peran dan status akun tetap sama.</DialogDescription></DialogHeader>
    <form className="action-form" method="post" noValidate onSubmit={submit} aria-busy={busy}>
      <div className="action-form-body">
        {retry && !busy && <Alert><AlertDescription>Hasil permintaan sebelumnya belum dipastikan. Isian dikunci agar Anda dapat memeriksa ulang permintaan yang sama. Kata sandi tidak disimpan di perangkat.</AlertDescription></Alert>}
        <FieldGroup>
          <Field data-disabled={locked} data-invalid={Boolean(errors.currentPassword)}><FieldLabel htmlFor="admin-reset-currentPassword">Kata sandi administrator Anda</FieldLabel><Input id="admin-reset-currentPassword" type="password" autoComplete="current-password" value={values.currentPassword} onChange={event => edit("currentPassword", event.target.value)} required maxLength={128} disabled={locked} aria-invalid={Boolean(errors.currentPassword)} aria-describedby={errors.currentPassword ? "admin-reset-currentPassword-error" : undefined}/>{errors.currentPassword && <FieldError id="admin-reset-currentPassword-error">{errors.currentPassword}</FieldError>}</Field>
          <Field data-disabled={locked} data-invalid={Boolean(errors.newPassword)}><FieldLabel htmlFor="admin-reset-newPassword">Kata sandi baru untuk {target.name}</FieldLabel><Input id="admin-reset-newPassword" type="password" autoComplete="new-password" value={values.newPassword} onChange={event => edit("newPassword", event.target.value)} required minLength={12} maxLength={128} disabled={locked} aria-invalid={Boolean(errors.newPassword)} aria-describedby={"admin-reset-newPassword-help" + (errors.newPassword ? " admin-reset-newPassword-error" : "")}/><FieldDescription id="admin-reset-newPassword-help">Gunakan 12–128 karakter. Sampaikan langsung kepada pemilik akun melalui jalur privat.</FieldDescription>{errors.newPassword && <FieldError id="admin-reset-newPassword-error">{errors.newPassword}</FieldError>}</Field>
          <Field data-disabled={locked} data-invalid={Boolean(errors.confirm)}><FieldLabel htmlFor="admin-reset-confirm">Ulangi kata sandi baru</FieldLabel><Input id="admin-reset-confirm" type="password" autoComplete="new-password" value={values.confirm} onChange={event => edit("confirm", event.target.value)} required maxLength={128} disabled={locked} aria-invalid={Boolean(errors.confirm)} aria-describedby={errors.confirm ? "admin-reset-confirm-error" : undefined}/>{errors.confirm && <FieldError id="admin-reset-confirm-error">{errors.confirm}</FieldError>}</Field>
          <Field data-disabled={locked} data-invalid={Boolean(errors.reason)}><FieldLabel htmlFor="admin-reset-reason">Alasan pengaturan ulang</FieldLabel><Textarea id="admin-reset-reason" value={values.reason} onChange={event => edit("reason", event.target.value)} required maxLength={500} rows={2} disabled={locked} aria-invalid={Boolean(errors.reason)} aria-describedby={"admin-reset-reason-help" + (errors.reason ? " admin-reset-reason-error" : "")}/><FieldDescription id="admin-reset-reason-help">Contoh: pemilik akun meminta bantuan karena lupa kata sandi. Jangan tulis kata sandi di alasan.</FieldDescription>{errors.reason && <FieldError id="admin-reset-reason-error">{errors.reason}</FieldError>}</Field>
        </FieldGroup>
        {error && <Alert ref={errorRef} tabIndex={-1} variant="destructive"><AlertDescription>{error}</AlertDescription></Alert>}
      </div>
      <DialogFooter className="action-form-actions"><Button type="button" variant="outline" disabled={busy} onClick={clearAndClose}>Tutup</Button><Button type="submit" disabled={busy}>{busy ? "Memperbarui…" : retry ? "Periksa kembali" : "Atur ulang & akhiri sesi"}</Button></DialogFooter>
    </form>
  </DialogContent></Dialog>;
}
