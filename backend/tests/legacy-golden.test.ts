import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { runCommand, validateState } from "../legacy/domain/engine";
import { emptyState } from "../legacy/domain/seed";
import { setTestClock } from "../legacy/domain/clock";
import { invoiceBalance, invoiceTotal, lineProgress, paymentAvailable, productAvailable } from "../legacy/domain/selectors";
import type { State } from "../legacy/domain/model";

const fixturePath = process.env.UNIT_TOKO_GOLDEN_FIXTURE || fileURLToPath(new URL("./Fixtures/legacy-scenarios.json", import.meta.url));
type Step = { actor:string; type:string; data:Record<string,unknown>; date?:string; bind?:string; capture?:Record<string,string>; reject?:string };
type Scenario = { key:string; sourceTest:string; initialOverrides?:Record<string,unknown>; initialAdds?:Record<string,unknown[]>; commands:Step[]; expected:Record<string,unknown> };
const date = "2026-09-17";
const dueDate = "2026-10-17";
const step = (actor:string,type:string,data:Record<string,unknown>,extra:Partial<Step>={}):Step=>({actor,type,data,...extra});
const actors = [
  ["pic-a","pic","div-ops"],["kepala","kepala",null],["staf","staf",null],["kurir","kurir",null],
  ["penagihan","penagihan",null],["penagihan-checker","penagihan",null],["laporan","laporan",null],["pimpinan","pimpinan",null],["akuntansi","akuntansi",null]
] as const;
const initialState:State = {
  ...emptyState(),
  users:actors.map(([id,role,divisionId])=>({id,name:id,role,divisionId,active:true})),
  divisions:[{id:"div-ops",name:"Ops",code:"OPS",address:"Jakarta"}],
  products:[{id:"p",name:"Produk uji",sku:"TEST",category:"OMI",unit:"pcs",price:10000,minimum:5,returnMonths:1,active:true}],
  suppliers:[{id:"supplier",name:"Pemasok uji",category:"OMI"}],
  batches:[{id:"b",productId:"p",code:"BATCH",qty:20,held:0,cost:6000,expiry:"2028-01-01",location:"Toko"}],
  journals:[{id:"opening",date:"2026-09-01",sourceId:"opening",description:"Saldo awal"}],
  journalLines:[
    {id:"opening-debit",journalId:"opening",account:"inventory",debit:120000,credit:0},
    {id:"opening-credit",journalId:"opening",account:"capital",debit:0,credit:120000}
  ],
  periods:[{id:"2026-09",revision:0,status:"open",approvedRevision:null,approvedBy:null,submittedBy:null,closedBy:null,closedAt:"",snapshot:null}]
};
const order = (qty:number) => [
  step("pic-a","order.create",{neededAt:date,lines:[{productId:"p",qty}]},{bind:"order",capture:{line:"orderLines[orderId=$order,productId=p].id"}}),
  step("kepala","order.review",{id:"$order",approve:true})
];
const shipment = (index:number,qty:number) => [
  step("staf","shipment.create",{orderId:"$order",courierId:"kurir",lines:[{orderLineId:"$line",qty}]},{bind:`shipment${index}`,capture:{[`shipmentLine${index}`]:`shipmentLines[shipmentId=$shipment${index},orderLineId=$line].id`}}),
  step("kurir","shipment.dispatch",{id:`$shipment${index}`}),
  step("pic-a","shipment.receive",{id:`$shipment${index}`,lines:[{id:`$shipmentLine${index}`,accepted:qty}]}),
  step("staf","sale.finalize",{id:`$shipment${index}`}),
  step("penagihan","invoice.issue",{divisionId:"div-ops",dueDate,lines:[{shipmentLineId:`$shipmentLine${index}`}]},{bind:`invoice${index}`})
];
const scenarios:Scenario[] = [
  {
    key:"partial-delivery",sourceTest:"U02",
    initialOverrides:{"batches[id=b].qty":8,"journalLines[id=opening-debit].debit":48000,"journalLines[id=opening-credit].credit":48000},
    commands:[
      ...order(10),step("staf","stock.reserve",{orderLineId:"$line",qty:8}),...shipment(1,8),
      step("staf","purchase.create",{kind:"DDO",supplierId:"supplier",lines:[{productId:"p",qty:10,cost:6000}]},{bind:"purchase",capture:{purchaseLine:"purchaseLines[purchaseId=$purchase,productId=p].id"}}),
      step("staf","purchase.confirm",{id:"$purchase"}),
      step("staf","purchase.receive",{id:"$purchase",lines:[{purchaseLineId:"$purchaseLine",qty:2}]}),
      step("staf","stock.reserve",{orderLineId:"$line",qty:2}),...shipment(2,2)
    ],
    expected:{"orders.length":1,"shipments.length":2,"invoices.length":2,"orderRemaining:$line":0,"invoiceTotal:$invoice1":80000,"invoiceTotal:$invoice2":20000,"invoiceBalance:$invoice1":80000,"invoiceBalance:$invoice2":20000,"shipmentLines[id=$shipmentLine1].accepted":8,"shipmentLines[id=$shipmentLine2].accepted":2,"accountNet:inventory":0,"accountNet:receivable":100000,"journalBalanced":true}
  },
  {
    key:"substitution-consent",sourceTest:"U03",
    initialAdds:{"products":[{...initialState.products[0],id:"q",sku:"Q",price:12000}]},
    commands:[
      ...order(10),step("staf","stock.reserve",{orderLineId:"$line",qty:8}),
      step("staf","shipment.create",{orderId:"$order",courierId:"kurir",lines:[{orderLineId:"$line",qty:8}]},{bind:"shipment",capture:{shipmentLine:"shipmentLines[shipmentId=$shipment,orderLineId=$line].id"}}),
      step("kurir","shipment.dispatch",{id:"$shipment"}),
      step("staf","substitution.propose",{orderLineId:"$line",productId:"q",reason:"Sisa stok kosong"},{capture:{substitution:"substitutions[orderLineId=$line].id"}}),
      step("staf","stock.reserve",{orderLineId:"$line",qty:2},{reject:"konfirmasi"}),
      step("pic-a","substitution.decide",{id:"$substitution",approve:true},{capture:{replacementLine:"substitutions[id=$substitution].replacementLineId"}})
    ],
    expected:{"orderLines.length":2,"orderLines[id=$line].productId":"p","orderLines[id=$line].cancelled":2,"orderLines[id=$replacementLine].productId":"q","orderLines[id=$replacementLine].qty":2,"orderLines[id=$replacementLine].price":12000,"shipmentLines[id=$shipmentLine].price":10000,"journalBalanced":true}
  },
  {
    key:"unique-invoice-source",sourceTest:"duplicate invoice",
    commands:[
      ...order(8),step("staf","stock.reserve",{orderLineId:"$line",qty:8}),...shipment(1,8),
      step("penagihan","invoice.issue",{divisionId:"div-ops",dueDate,lines:[{shipmentLineId:"$shipmentLine1"}]},{reject:"sudah ditagih"})
    ],
    expected:{"invoices.length":1,"invoiceLines.length":1,"invoiceTotal:$invoice1":80000,"invoiceBalance:$invoice1":80000,"accountNet:receivable":80000,"journalBalanced":true}
  },
  {
    key:"payment-allocation",sourceTest:"U08",
    commands:[
      ...order(12),step("staf","stock.reserve",{orderLineId:"$line",qty:12}),...shipment(1,8),...shipment(2,4),
      step("penagihan","payment.record",{divisionId:"div-ops",amount:125000,payer:"OPS"},{bind:"payment"}),
      step("penagihan-checker","payment.verify",{id:"$payment"}),
      step("penagihan","payment.allocate",{paymentId:"$payment",lines:[{invoiceId:"$invoice1",amount:80000},{invoiceId:"$invoice2",amount:40000}]}),
      step("penagihan","payment.allocate",{paymentId:"$payment",lines:[{invoiceId:"$invoice1",amount:80000}]},{reject:"melebihi"})
    ],
    expected:{"invoices.length":2,"allocations.length":2,"invoiceBalance:$invoice1":0,"invoiceBalance:$invoice2":0,"paymentAvailable:$payment":5000,"payments[id=$payment].status":"verified","payments[id=$payment].verifiedBy":"penagihan-checker","accountNet:cash":125000,"accountNet:receivable":0,"accountNet:unallocated":-5000,"journalSourceCount:$payment":1,"journalBalanced":true}
  },
  {
    key:"damaged-return",sourceTest:"U04",
    commands:[
      ...order(10),step("staf","stock.reserve",{orderLineId:"$line",qty:8}),
      step("staf","shipment.create",{orderId:"$order",courierId:"kurir",lines:[{orderLineId:"$line",qty:8}]},{bind:"shipment",capture:{shipmentLine:"shipmentLines[shipmentId=$shipment,orderLineId=$line].id"}}),
      step("kurir","shipment.dispatch",{id:"$shipment"}),
      step("pic-a","shipment.receive",{id:"$shipment",lines:[{id:"$shipmentLine",accepted:8}]}),
      step("pic-a","complaint.create",{shipmentLineId:"$shipmentLine",qty:1,reason:"Rusak"},{bind:"complaint"}),
      step("staf","sale.finalize",{id:"$shipment"},{reject:"komplain"}),
      step("staf","shipment.return",{shipmentLineId:"$shipmentLine",qty:1,good:false,reason:"Rusak"}),
      step("staf","stock.returnCreate",{batchId:"b",qty:1,fromHeld:true,reason:"Barang rusak dari divisi"},{bind:"return"}),
      step("staf","stock.returnResolve",{id:"$return",accept:true,note:"Diterima pemasok"}),
      step("staf","complaint.resolve",{id:"$complaint",resolution:"Dikembalikan"}),
      step("staf","sale.finalize",{id:"$shipment"}),
      step("penagihan","invoice.issue",{divisionId:"div-ops",dueDate,lines:[{shipmentLineId:"$shipmentLine"}]},{bind:"invoice"})
    ],
    expected:{"shipmentLines[id=$shipmentLine].accepted":7,"batches[id=b].qty":12,"batches[id=b].held":0,"invoiceTotal:$invoice":70000,"invoiceBalance:$invoice":70000,"accountNet:inventory":72000,"accountNet:cogs":42000,"accountNet:supplierReceivable":6000,"accountNet:receivable":70000,"journalBalanced":true}
  },
  {
    key:"stocktake-approval",sourceTest:"U10",
    initialOverrides:{"batches[id=b].expiry":"2026-09-01"},
    commands:[
      step("staf","stock.returnCreate",{batchId:"b",qty:2,reason:"Kedaluwarsa"},{bind:"return"}),
      step("staf","stock.returnResolve",{id:"$return",accept:false,note:"Di luar kebijakan"}),
      step("kepala","stock.holdRelease",{id:"$return",reason:"Layak"},{reject:"kedaluwarsa"}),
      step("staf","stocktake.create",{batchId:"b",counted:19,reason:"Sudah hitung ulang"},{bind:"stocktake"}),
      step("staf","stocktake.approve",{id:"$stocktake"},{reject:"akses"}),
      step("kepala","stocktake.approve",{id:"$stocktake"}),
      step("kepala","stocktake.approve",{id:"$stocktake"},{reject:"berubah|diproses"})
    ],
    expected:{"batches[id=b].qty":19,"batches[id=b].held":2,"stocktakes[id=$stocktake].status":"approved","stocktakes.length":1,"sellable:p":0,"journalBalanced":true}
  },
  {
    key:"period-close-lock",sourceTest:"U12",
    commands:[
      ...order(8),step("staf","stock.reserve",{orderLineId:"$line",qty:8}),...shipment(1,8),
      step("laporan","period.submit",{month:"2026-09"}),
      step("pimpinan","period.approve",{id:"2026-09"}),
      step("akuntansi","period.close",{id:"2026-09"}),
      step("penagihan","payment.record",{amount:1000,payer:"X"},{date:"2026-08-01",reject:"Periode sudah ditutup"})
    ],
    expected:{"periods[id=2026-09].status":"closed","periods[id=2026-09].submittedBy":"laporan","periods[id=2026-09].approvedBy":"pimpinan","periods[id=2026-09].closedBy":"akuntansi","periods[id=2026-09].snapshot.debit":328000,"periods[id=2026-09].snapshot.credit":328000,"snapshotAccountNet:2026-09:receivable":80000,"invoiceBalance:$invoice1":80000,"payments.length":0,"journalBalanced":true}
  }
];
const source = {schemaVersion:2,kind:"legacy-command-golden",clock:"2026-09-17T05:00:00.000Z",businessDate:date,timezone:"Asia/Jakarta",amountUnit:"IDR integer",referenceSyntax:"$name binds a prior result or captured state ID; collection[field=value,...].property selects exactly one record independently of array order",initialState,scenarios};
if (process.argv.includes("--write-fixture")) {
  writeFileSync(fixturePath,JSON.stringify(source,null,2)+"\n");
} else {
  const fixture = JSON.parse(readFileSync(fixturePath,"utf8"));
  assert.deepEqual(fixture,source,"Committed golden JSON must match the independently maintained scenario definitions");
  setTestClock(()=>new Date(fixture.clock));
  const path = (object:any,key:string,refs:Record<string,string>):any => {
    const selected=/^([A-Za-z]+)\[([^\]]+)\](?:\.(.+))?$/.exec(key);
    if(selected){
      const [,collection,filter,field]=selected;
      const rows=object[collection];
      assert.ok(Array.isArray(rows),`Unknown collection ${collection}`);
      const matches=rows.filter((row:any)=>filter.split(",").every(condition=>{
        const [name,raw]=condition.split("=");
        assert.ok(name&&raw,`Invalid selector ${key}`);
        const expected=raw.startsWith("$")?refs[raw.slice(1)]:raw;
        assert.ok(expected!==undefined,`Unbound selector reference ${raw}`);
        return String(row[name])===expected;
      }));
      assert.equal(matches.length,1,`Selector ${key} must identify exactly one record`);
      return field?field.split(".").reduce((value,part)=>value?.[part],matches[0]):matches[0];
    }
    return key.split(".").reduce((value,part)=>value?.[part],object);
  };
  const setPath = (object:any,key:string,value:any,refs:Record<string,string>) => {
    const dot=key.lastIndexOf(".");
    assert.ok(dot>0,`Override needs a field: ${key}`);
    const parent=path(object,key.slice(0,dot),refs);
    parent[key.slice(dot+1)]=value;
  };
  const resolve = (value:any,refs:Record<string,string>):any =>
    typeof value==="string"&&value.startsWith("$") ? (assert.ok(refs[value.slice(1)],`Unbound reference ${value}`),refs[value.slice(1)]) :
    Array.isArray(value)?value.map(item=>resolve(item,refs)):
    value&&typeof value==="object"?Object.fromEntries(Object.entries(value).map(([key,item])=>[key,resolve(item,refs)])):value;
  function observed(state:State,key:string,refs:Record<string,string>):unknown {
    const [kind,ref]=key.split(":");
    if (kind==="invoiceTotal") return invoiceTotal(state,refs[ref.slice(1)]);
    if (kind==="invoiceBalance") return invoiceBalance(state,refs[ref.slice(1)]);
    if (kind==="paymentAvailable") return paymentAvailable(state,refs[ref.slice(1)]);
    if (kind==="orderRemaining") return lineProgress(state,state.orderLines.find(item=>item.id===refs[ref.slice(1)])!).remaining;
    if (kind==="sellable") return productAvailable(state,ref,date);
    if (kind==="accountNet") return state.journalLines.filter(line=>line.account===ref).reduce((net,line)=>net+line.debit-line.credit,0);
    if (kind==="journalSourceCount") return state.journals.filter(journal=>journal.sourceId===refs[ref.slice(1)]).length;
    if (kind==="snapshotAccountNet") {
      const [month,account]=key.split(":").slice(1);
      const snapshot=path(state,`periods[id=${month}].snapshot`,refs);
      const row=snapshot.accounts.find((item:any)=>item.code===account);
      assert.ok(row,`Missing snapshot account ${account}`);
      return row.debit-row.credit;
    }
    if (kind==="journalBalanced") return state.journals.every(journal=>{
      const lines=state.journalLines.filter(line=>line.journalId===journal.id);
      return lines.reduce((n,line)=>n+line.debit,0)===lines.reduce((n,line)=>n+line.credit,0);
    });
    return path(state,key,refs);
  }
  for (const scenario of fixture.scenarios as Scenario[]) test(`legacy golden: ${scenario.key}`,()=>{
    for(const reorder of [false,true]){
      let state:State=structuredClone(fixture.initialState);
      const refs:Record<string,string>={};
      for(const [collection,rows] of Object.entries(scenario.initialAdds||{})){
        assert.ok(Array.isArray((state as any)[collection]),`Unknown initial collection ${collection}`);
        (state as any)[collection].push(...structuredClone(rows));
      }
      for(const [key,value] of Object.entries(scenario.initialOverrides||{}))setPath(state,key,value,refs);
      validateState(state);
      for(const [index,command] of scenario.commands.entries()){
        const actor=state.users.find(user=>user.id===command.actor);
        assert.ok(actor,`${scenario.key} step ${index} actor`);
        const before=structuredClone(state);
        const data=resolve(command.data,refs);
        const apply=()=>runCommand(state,actor,{id:`golden-${scenario.key}-${index}`,type:command.type,date:command.date||date,data});
        if(command.reject){
          assert.throws(apply,new RegExp(command.reject),`${scenario.key} step ${index} rejection`);
          assert.deepEqual(state,before,`${scenario.key} step ${index} rejected command changed state`);
        } else {
          const output=apply();state=output.state;
          if(command.bind)refs[command.bind]=output.result.id;
          if(reorder)for(const value of Object.values(state))if(Array.isArray(value))value.reverse();
          for(const [name,selector] of Object.entries(command.capture||{}))refs[name]=path(state,selector,refs);
          validateState(state);
        }
      }
      for(const [key,want] of Object.entries(scenario.expected))assert.deepEqual(observed(state,key,refs),want,`${scenario.key} expected ${key} (reordered=${reorder})`);
    }
  });
}
