"use client";

import {useEffect, useRef, useState} from "react";
import {toast} from "sonner";
import {Button} from "@/components/ui/button";
import {Checkbox} from "@/components/ui/checkbox";
import {Input} from "@/components/ui/input";
import {Field, FieldDescription, FieldGroup, FieldLabel, FieldSet, FieldLegend} from "@/components/ui/field";
import {Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle} from "@/components/ui/dialog";
import {billableRows, invoiceSelection, MAX_INVOICE_LINES, suggestedInvoiceDates} from "@/lib/domain/invoice-selection";
import {money} from "@/lib/domain/selectors";
import {requestJson} from "@/lib/client/requests";
import type {CommandResult} from "@/lib/domain/model";
import type {WorkspaceContext} from "./workspace";

export function InvoiceComposer({ctx,close}: {ctx:WorkspaceContext;close:()=>void}) {
  const {s,actor,refresh,go}=ctx;
  const rows=billableRows(s);
  const [division,setDivision]=useState("");
  const [ids,setIds]=useState<string[]>([]);
  const [query,setQuery]=useState("");
  const [page,setPage]=useState(1);
  const [initialDates]=useState(()=>suggestedInvoiceDates(s));
  const [date,setDate]=useState(initialDates.date);
  const [due,setDue]=useState(initialDates.dueDate);
  const [commandId,setCommandId]=useState(()=>crypto.randomUUID());
  const [busy,setBusy]=useState(false),[error,setError]=useState("");
  const errorRef=useRef<HTMLParagraphElement>(null);
  const submitting=useRef(false);
  const result=invoiceSelection(s,division,ids);
  const search=query.trim().toLocaleLowerCase("id-ID");
  const filtered=rows.filter(row=>row.divisionId===division && (!search || `${row.product} ${row.sku} ${row.shipmentNumber} ${row.orderNumber}`.toLocaleLowerCase("id-ID").includes(search)));
  const pages=Math.max(1,Math.ceil(filtered.length/25)), current=Math.min(page,pages);
  const visible=filtered.slice((current-1)*25,current*25);
  const newVisible=visible.filter(row=>!ids.includes(row.id));
  const canSelectPage=Boolean(newVisible.length)&&ids.length+newVisible.length<=MAX_INVOICE_LINES;
  useEffect(()=>{if(error)errorRef.current?.focus();},[error]);
  function changed(){setCommandId(crypto.randomUUID());setError("");}
  function toggle(id:string,checked:boolean){changed();setIds(previous=>checked?[...previous,id]:previous.filter(item=>item!==id));}
  async function submit(event:React.FormEvent){
    event.preventDefault();
    if(submitting.current)return;
    const problem=result.error || (date<result.minimumDate?`Tanggal invoice minimal ${result.minimumDate}, sesuai periode terbuka dan penerimaan final.`:"") || (due<date?"Jatuh tempo tidak boleh sebelum tanggal invoice.":"");
    if(problem){setError(problem);return;}
    submitting.current=true;setBusy(true);setError("");
    try{
      const response=await requestJson<CommandResult>("/api/commands",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({id:commandId,type:"invoice.issue",date,data:{divisionId:division,dueDate:due,taxBps:0,lines:ids.map(shipmentLineId=>({shipmentLineId}))}})});
      await refresh();toast.success(response.message);close();go("billing",response.id,{balance:null});
    }catch(cause){setError(cause instanceof Error?cause.message:"Invoice belum diterbitkan. Pilihan tetap tersimpan.");await refresh();}
    finally{submitting.current=false;setBusy(false);}
  }
  return <Dialog open onOpenChange={open=>{if(!open&&!busy)close();}}><DialogContent className="invoice-composer sm:max-w-3xl" onEscapeKeyDown={event=>{if(busy)event.preventDefault();}}>
    <DialogHeader><DialogTitle>Terbitkan invoice divisi</DialogTitle><DialogDescription>Pilih penerimaan final yang akan ditagih. Satu invoice dapat memuat maksimal {MAX_INVOICE_LINES} baris.</DialogDescription></DialogHeader>
    {actor.role!=="penagihan"?<p>Hanya Bagian Penagihan dapat menerbitkan invoice.</p>:<form onSubmit={submit} aria-busy={busy}>
      <div className="invoice-composer-body">
        <FieldGroup className="invoice-composer-fields">
          <Field><FieldLabel htmlFor="invoice-division">Divisi pemesan</FieldLabel><select id="invoice-division" className="native-select" required value={division} disabled={busy} onChange={event=>{setDivision(event.target.value);setIds([]);setPage(1);changed();}}><option value="">Pilih divisi…</option>{s.divisions.filter(item=>rows.some(row=>row.divisionId===item.id)).map(item=><option value={item.id} key={item.id}>{item.name}</option>)}</select></Field>
          <Field><FieldLabel htmlFor="invoice-date">Tanggal invoice</FieldLabel><Input id="invoice-date" type="date" required value={date} min={result.minimumDate||undefined} disabled={busy} onChange={event=>{setDate(event.target.value);changed();}}/>{result.minimumDate&&<FieldDescription>Tanggal minimal {new Date(`${result.minimumDate}T12:00:00`).toLocaleDateString("id-ID",{day:"numeric",month:"short",year:"numeric"})}, mengikuti periode terbuka dan penerimaan final.</FieldDescription>}</Field>
          <Field><FieldLabel htmlFor="invoice-due">Jatuh tempo</FieldLabel><Input id="invoice-due" type="date" required value={due} min={date} disabled={busy} onChange={event=>{setDue(event.target.value);changed();}}/></Field>
        </FieldGroup>
        {division&&<FieldSet className="invoice-selection"><FieldLegend>Penerimaan yang akan ditagih</FieldLegend>
          <Field><FieldLabel htmlFor="invoice-search">Cari barang, SKU, atau dokumen</FieldLabel><Input id="invoice-search" type="search" value={query} disabled={busy} onChange={event=>{setQuery(event.target.value);setPage(1);}}/><FieldDescription>{filtered.length} baris sesuai pencarian. Pilihan dari halaman lain tetap disimpan.</FieldDescription></Field>
          <div className="invoice-selection-tools"><Button type="button" variant="outline" size="sm" disabled={busy||!canSelectPage} onClick={()=>{setIds(previous=>[...previous,...newVisible.map(row=>row.id)]);changed();}}>{newVisible.length ? `Pilih ${newVisible.length} baris di halaman ini` : visible.length ? "Semua baris di halaman ini dipilih" : "Tidak ada baris untuk dipilih"}</Button><Button type="button" variant="ghost" size="sm" disabled={busy||!ids.length} onClick={()=>{setIds([]);changed();}}>Hapus semua pilihan</Button></div>
          {!canSelectPage&&newVisible.length>0&&<p className="muted">{ids.length>=MAX_INVOICE_LINES?"Batas 100 baris tercapai. Terbitkan invoice ini, lalu buat invoice berikutnya untuk sisa penerimaan.":`Pilih baris satu per satu untuk mengisi sisa ${MAX_INVOICE_LINES-ids.length} tempat pada invoice.`}</p>}
          <div className="invoice-line-list">{visible.map(row=><label className="invoice-select-row" key={row.id} htmlFor={`invoice-line-${row.id}`}><Checkbox id={`invoice-line-${row.id}`} checked={ids.includes(row.id)} disabled={busy||(!ids.includes(row.id)&&ids.length>=MAX_INVOICE_LINES)} onCheckedChange={checked=>toggle(row.id,checked===true)}/><span><strong>{row.product}</strong><small>{row.sku} · {row.qty} {row.unit} × {money(row.price)}</small><small>{row.shipmentNumber} · {row.orderNumber}</small></span><b>{money(row.value)}</b></label>)}</div>
          {!visible.length&&<p className="module-note">Tidak ada penerimaan yang sesuai. Coba ubah pencarian.</p>}
          <div className="invoice-pagination"><Button type="button" variant="outline" size="sm" disabled={busy||current<=1} onClick={()=>setPage(current-1)}>Sebelumnya</Button><span>Halaman {current} dari {pages}</span><Button type="button" variant="outline" size="sm" disabled={busy||current>=pages} onClick={()=>setPage(current+1)}>Berikutnya</Button></div>
        </FieldSet>}
        <section className="invoice-selection-summary" aria-live="polite"><h3>Ringkasan invoice</h3><dl><div><dt>Baris dipilih</dt><dd>{ids.length} / {MAX_INVOICE_LINES}</dd></div><div><dt>Total invoice</dt><dd>{money(result.total)}</dd></div><div><dt>Sisa belum dipilih</dt><dd>{result.remaining} baris</dd></div></dl><p>Pajak demo nonaktif. Sisa penerimaan dapat diterbitkan pada invoice berikutnya.</p></section>
        {error&&<p ref={errorRef} tabIndex={-1} role="alert" className="error-message">{error}</p>}
      </div>
      <DialogFooter className="invoice-composer-actions"><Button type="button" variant="outline" disabled={busy} onClick={close}>Batal</Button><Button type="submit" disabled={busy||!ids.length}>{busy?"Menerbitkan…":"Terbitkan invoice"}</Button></DialogFooter>
    </form>}
  </DialogContent></Dialog>;
}
