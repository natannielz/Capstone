"use client";

import {useState} from "react";
import {FileText} from "lucide-react";
import {toast} from "sonner";
import {Button} from "@/components/ui/button";
import {Input} from "@/components/ui/input";
import {Field, FieldDescription, FieldError, FieldGroup, FieldLabel} from "@/components/ui/field";
import {Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle} from "@/components/ui/dialog";
import {invoiceBalance, money, paymentAvailable, sum, today} from "@/lib/domain/selectors";
import {buyerKey, buyerLabel} from "@/lib/domain/buyers";
import {allocationInvoices} from "@/lib/domain/invoice-selection";
import type {Payment, State} from "@/lib/domain/model";
import type {WorkspaceContext} from "./workspace";

function initialDate(s: State, payment?: Payment) {
  let date = today();
  const lastClosed = s.periods.filter(period => period.status === "closed").map(period => period.id).sort().at(-1);
  if (lastClosed && lastClosed >= date.slice(0, 7)) {
    const [year, month] = lastClosed.split("-").map(Number);
    date = new Date(Date.UTC(year, month, 1)).toISOString().slice(0, 10);
  }
  return [date, payment?.verifiedDate || payment?.date || date].sort().at(-1)!;
}

function parseAmount(value: string | undefined) {
  if (!value) return 0;
  if (!/^\d+$/.test(value)) return null;
  const amount = Number(value);
  return Number.isSafeInteger(amount) ? amount : null;
}

export function PaymentAllocation({ctx, paymentId, close}: {ctx: WorkspaceContext; paymentId: string; close: () => void}) {
  const {s, actor, refresh} = ctx;
  const payment = s.payments.find(item => item.id === paymentId);
  const [amounts, setAmounts] = useState<Record<string, string>>({});
  const [date, setDate] = useState(() => initialDate(s, payment));
  const [commandId, setCommandId] = useState(() => crypto.randomUUID());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const allowed = actor.role === "penagihan" && payment?.status === "verified" && Boolean(buyerKey(payment));
  const available = payment ? paymentAvailable(s, payment.id) : 0;
  const invoices = payment ? allocationInvoices(s,payment,Object.keys(amounts).filter(id=>Boolean(amounts[id]))) : [];
  const rows = invoices.map(invoice => {
    const amount = parseAmount(amounts[invoice.id]), balance = invoiceBalance(s, invoice.id);
    const problem = amount === null ? "Masukkan jumlah rupiah bulat, minimal 0."
      : amount > balance ? `Maksimal ${money(balance)}, sesuai sisa invoice.` : "";
    return {invoice, amount, balance, problem};
  });
  const total = sum(rows.map(row => row.amount || 0));
  const remaining = available - total;
  const unavailableSelection = Object.entries(amounts).some(([id, value]) => Boolean(value) && !invoices.some(invoice => invoice.id === id));
  const selectedCount = rows.filter(row => row.amount && row.amount > 0).length;
  const hasProblems = rows.some(row => row.problem) || remaining < 0 || unavailableSelection || selectedCount > 100;

  function updateAmount(id: string, value: string) {
    setAmounts(previous => ({...previous, [id]: value}));
    setCommandId(crypto.randomUUID());
    setError("");
  }

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    if (!allowed || !payment || busy || hasProblems || total <= 0) return;
    setBusy(true);
    setError("");
    try {
      const response = await fetch("/api/commands", {
        method: "POST", headers: {"Content-Type": "application/json"},
        body: JSON.stringify({id: commandId, type: "payment.allocate", date, data: {
          paymentId, lines: rows.filter(row => row.amount && row.amount > 0).map(row => ({invoiceId: row.invoice.id, amount: row.amount})),
        }}),
      });
      const result = await response.json() as {error?: string; message?: string};
      if (!response.ok) {
        await refresh();
        throw new Error(result.error || "Alokasi belum disimpan. Periksa saldo terbaru lalu coba kembali.");
      }
      await refresh();
      toast.success(result.message || "Pembayaran dialokasikan.");
      close();
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : "Koneksi bermasalah. Periksa kembali sebelum mencoba lagi.");
    } finally {
      setBusy(false);
    }
  }

  return <Dialog open onOpenChange={open => {if (!open && !busy) close();}}>
    <DialogContent className="allocation-v5-dialog sm:max-w-2xl" onEscapeKeyDown={event => {if (busy) event.preventDefault();}}>
      <DialogHeader><DialogTitle>Alokasikan pembayaran</DialogTitle><DialogDescription>Bagi dana ke invoice pembeli yang sama. Periksa saldo akhir sebelum menyimpan.</DialogDescription></DialogHeader>
      {!allowed ? <div className="allocation-v5-unavailable"><p>Alokasi hanya tersedia untuk Bagian Penagihan setelah dana diverifikasi dan pembayar diketahui.</p><Button variant="outline" onClick={close}>Tutup</Button></div> : <form onSubmit={submit} className="allocation-v5-form" aria-busy={busy}>
        <div className="allocation-v5-body">
          <div className="allocation-v5-source"><strong>{payment!.payer}</strong><p>{buyerLabel(s,payment!)}</p><span>Referensi: {payment!.reference || "Tanpa referensi"}</span></div>
          <FieldGroup>
            <Field><FieldLabel htmlFor="allocation-date">Tanggal pencatatan</FieldLabel><Input id="allocation-date" type="date" required value={date} disabled={busy} onChange={event => {setDate(event.target.value); setCommandId(crypto.randomUUID()); setError("");}}/></Field>
            <div className="allocation-v5-invoices">
              {rows.map(({invoice, amount, balance, problem}) => <section className="allocation-v5-invoice" key={invoice.id}>
                <div className="allocation-v5-invoice-title"><div><h3>{invoice.number}</h3><p>Jatuh tempo {new Date(`${invoice.dueDate}T12:00:00`).toLocaleDateString("id-ID", {day: "numeric", month: "short", year: "numeric"})}</p></div>
                  <a href={`/api/documents/invoice/${encodeURIComponent(invoice.id)}`} target="_blank" rel="noreferrer" aria-label={`Buka dokumen ${invoice.number} di tab baru`}><FileText size={17} aria-hidden="true"/>Dokumen</a>
                </div>
                <Field data-invalid={Boolean(problem)} data-disabled={busy}>
                  <FieldLabel htmlFor={`allocation-${invoice.id}`}>Alokasi untuk {invoice.number} (Rp)</FieldLabel>
                  <Input id={`allocation-${invoice.id}`} type="text" inputMode="numeric" autoComplete="off" placeholder="0" value={amounts[invoice.id] || ""} disabled={busy}
                    aria-invalid={Boolean(problem)} aria-describedby={`${invoice.id}-balance${problem ? ` ${invoice.id}-error` : ""}`}
                    onChange={event => updateAmount(invoice.id, event.target.value)} onFocus={event => event.target.select()}/>
                  <FieldDescription id={`${invoice.id}-balance`}>Sisa invoice {money(balance)}. Kosongkan jika tidak dialokasikan.</FieldDescription>
                  {problem && <FieldError id={`${invoice.id}-error`}>{problem}</FieldError>}
                </Field>
                <dl className="allocation-v5-row-summary"><div><dt>Sisa sebelum alokasi</dt><dd>{money(balance)}</dd></div><div><dt>Sisa sesudah alokasi</dt><dd data-invalid={Boolean(problem)}>{amount === null ? "Periksa jumlah" : money(balance - amount)}</dd></div></dl>
              </section>)}
              {!rows.length && <p className="allocation-v5-empty">Tidak ada invoice belum lunas untuk pembeli ini. Dana tetap tercatat sebagai saldo belum dialokasikan.</p>}
            </div>
          </FieldGroup>
          <div className="allocation-v5-preview" aria-live="polite"><h3>Ringkasan alokasi</h3><dl>
            <div><dt>Dana tersedia</dt><dd>{money(available)}</dd></div><div><dt>Total alokasi</dt><dd>{money(total)}</dd></div><div><dt>Dana tersisa</dt><dd data-invalid={remaining < 0}>{money(remaining)}</dd></div>
          </dl><p>Dana tersisa tetap menjadi saldo pembeli dan dapat digunakan untuk invoice berikutnya.</p></div>
          {remaining < 0 && <FieldError>Total alokasi melebihi dana tersedia sebesar {money(-remaining)}. Kurangi jumlah alokasi.</FieldError>}
          {selectedCount > 100 && <FieldError>Maksimal 100 invoice dalam satu alokasi. Kosongkan sebagian pilihan; sisa dana dapat dialokasikan berikutnya.</FieldError>}
          {unavailableSelection && <FieldError>Invoice yang dipilih sudah tidak tersedia. Tutup formulir, muat ulang pembayaran, lalu periksa kembali.</FieldError>}
          {error && <p role="alert" className="error-message">{error} Jumlah isian tetap tersimpan; periksa saldo dan tanggal sebelum mencoba lagi.</p>}
        </div>
        <DialogFooter className="allocation-v5-footer"><Button type="button" variant="outline" disabled={busy} onClick={close}>Batal</Button><Button type="submit" disabled={busy || hasProblems || total <= 0}>{busy ? "Menyimpan…" : "Simpan alokasi"}</Button></DialogFooter>
      </form>}
    </DialogContent>
  </Dialog>;
}
