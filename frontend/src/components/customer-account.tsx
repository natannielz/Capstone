"use client";

import {useEffect, useRef, useState, type FormEvent} from "react";
import {Button} from "./ui/button";
import {Input} from "./ui/input";
import {Textarea} from "./ui/textarea";
import {Alert, AlertDescription} from "./ui/alert";
import {Field, FieldDescription, FieldGroup, FieldLabel} from "./ui/field";
import {Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle} from "./ui/dialog";
import type {CommandResult} from "@/lib/domain/model";
import {ApiError, requestJson} from "@/lib/client/requests";
import {ambiguousCustomerActionError, clearCustomerActionDraft, customerActionTarget, readCustomerActionDraft, saveCustomerActionDraft, type CustomerActionDraft, type CustomerActionField} from "@/lib/client/customer-action-draft";

type ActionField = CustomerActionField;
type CustomerAction = {title: string; description: string; type: string; data: Record<string, unknown>; fields: ActionField[]; map?: (values: Record<string, string>) => Record<string, unknown>; label?: string};

function CustomerActionDialog({actorId, action, close, complete}: {actorId: string; action: CustomerAction; close: () => void; complete: (result: CommandResult) => Promise<void>}) {
  const target = customerActionTarget(action.type, action.data);
  const [draft, setDraft] = useState(() => readCustomerActionDraft(actorId, target));
  const [values, setValues] = useState<Record<string, string>>(() => draft?.values || Object.fromEntries(action.fields.map(f => [f.key, String(f.value ?? "")])));
  const [commandId, setCommandId] = useState(() => crypto.randomUUID()), [busy, setBusy] = useState(false), [error, setError] = useState("");
  const [reviewing, setReviewing] = useState(false);
  const reviewRef = useRef<HTMLHeadingElement>(null);
  const pending = useRef(false), errorRef = useRef<HTMLDivElement>(null);
  const presentation = draft?.presentation || action;
  const locked = busy || Boolean(draft);
  useEffect(() => {if (error) errorRef.current?.focus();}, [error]);
  async function submit(event: FormEvent) {
    event.preventDefault(); if (pending.current) return;
    if (action.type === "shipment.receive" && !draft && !reviewing) {
      setReviewing(true); setError(""); requestAnimationFrame(() => reviewRef.current?.focus()); return;
    }
    pending.current = true; setBusy(true); setError("");
    let sent: CustomerActionDraft | undefined;
    try {
      sent = saveCustomerActionDraft(draft || {version: 1, actorId, target, path: window.location.pathname,
        command: {id: commandId, type: action.type, data: {...action.data, ...(action.map ? action.map(values) : values)}},
        baseData: action.data, values, presentation: {title: action.title, description: action.description, fields: action.fields, label: action.label}});
      setDraft(sent); setValues(sent.values);
      const result = await requestJson<CommandResult>("/api/commands", {method: "POST", headers: {"Content-Type": "application/json"}, body: JSON.stringify(sent.command)});
      if (!result || typeof result.id !== "string" || typeof result.message !== "string") throw new ApiError("Hasil tindakan belum dapat dipastikan. Coba periksa kembali permintaan yang sama.", 200);
      clearCustomerActionDraft(actorId, target, sent.command.id); setDraft(undefined);
      await complete(result);
    } catch (cause) {
      if (sent && !ambiguousCustomerActionError(cause)) {
        clearCustomerActionDraft(actorId, target, sent.command.id); setDraft(undefined);
      }
      if (cause instanceof ApiError && cause.status === 401) {
        window.location.assign(`/customer/login?next=${encodeURIComponent(window.location.pathname + window.location.search)}`); return;
      }
      setError(cause instanceof Error ? cause.message : "Tindakan belum berhasil. Isian Anda tetap tersimpan.");
    }
    finally {pending.current = false; setBusy(false);}
  }
  function edit(key: string, value: string) {
    if (locked || pending.current) return;
    setValues(previous => ({...previous, [key]: value})); setCommandId(crypto.randomUUID()); setError("");
  }
  return <Dialog open onOpenChange={open => {if (!open && !pending.current) close();}}><DialogContent className="customer-action-dialog" onEscapeKeyDown={event => {if (pending.current) event.preventDefault();}}><DialogHeader><DialogTitle>{presentation.title}</DialogTitle><DialogDescription>{presentation.description}</DialogDescription></DialogHeader><form onSubmit={submit} aria-busy={busy}>
    {draft && !busy && <Alert><AlertDescription>Hasil permintaan sebelumnya belum dipastikan. Isian dikunci agar tidak tercatat dua kali. Periksa kembali untuk melanjutkan permintaan yang sama, termasuk setelah halaman dimuat ulang.</AlertDescription></Alert>}
    {reviewing && !draft ? <div className="customer-receipt-review"><h3 ref={reviewRef} tabIndex={-1}>Periksa penerimaan Anda</h3><p>Penerima: <strong>{values.receiver}</strong></p><p>Pastikan jumlah sesuai barang yang benar-benar diterima.</p><ul>{presentation.fields.filter(field => field.key.startsWith("qty_")).map(field => {const received = Number(values[field.key]), sent = Number(field.max || 0), reason = values[`reason_${field.key.slice(4)}`]; return <li key={field.key}><strong>{field.label.replace(/ — diterima$/, "")}</strong><dl><div><dt>Dikirim</dt><dd>{sent}</dd></div><div><dt>Diterima</dt><dd>{received}</dd></div><div><dt>Selisih</dt><dd>{sent - received}</dd></div></dl>{reason && <p>Alasan: {reason}</p>}</li>;})}</ul></div> : <FieldGroup>{presentation.fields.map(f => <Field key={f.key} data-disabled={locked}><FieldLabel htmlFor={`customer-action-${f.key}`}>{f.label}</FieldLabel>{f.type === "textarea" ? <Textarea id={`customer-action-${f.key}`} value={values[f.key] || ""} disabled={locked} required={!f.optional} maxLength={1000} onChange={event => edit(f.key, event.target.value)}/> : <Input id={`customer-action-${f.key}`} type={f.type || "text"} min={f.min} max={f.max} step={f.type === "number" ? 1 : undefined} value={values[f.key] || ""} disabled={locked} required={!f.optional} maxLength={250} onChange={event => edit(f.key, event.target.value)}/>} {f.help && <FieldDescription>{f.help}</FieldDescription>}</Field>)}</FieldGroup>}
    {error && <Alert ref={errorRef} tabIndex={-1} variant="destructive"><AlertDescription>{error}</AlertDescription></Alert>}<DialogFooter><Button variant="outline" type="button" disabled={busy} onClick={() => {if (reviewing && !draft) {setReviewing(false); requestAnimationFrame(() => document.getElementById("customer-action-receiver")?.focus());} else close();}}>{draft ? "Tutup sementara" : reviewing ? "Ubah isian" : "Kembali"}</Button><Button type="submit" disabled={busy}>{busy ? "Menyimpan…" : draft ? "Periksa kembali" : action.type === "shipment.receive" ? reviewing ? "Konfirmasi penerimaan" : "Tinjau penerimaan" : presentation.label || "Simpan"}</Button></DialogFooter></form></DialogContent></Dialog>;
}
