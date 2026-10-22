import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {mkdir,writeFile} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
import {resolve,join} from 'node:path';

// Fixture-only browser UI evidence. Every API response below is synthetic.
// Run against an isolated frontend preview at this fixed loopback origin.
const require=createRequire(process.env.UNIT_TOKO_PLAYWRIGHT_PACKAGE
 ? resolve(process.env.UNIT_TOKO_PLAYWRIGHT_PACKAGE) : import.meta.url);
const {chromium}=require('playwright');
const outputDir=fileURLToPath(new URL('../../tmp/production/',import.meta.url));
await mkdir(outputDir,{recursive:true});
const outputFile=name=>join(outputDir,name);
const channel=process.env.UNIT_TOKO_BROWSER_CHANNEL||(process.platform==='win32'?'msedge':undefined);
const browser=await chromium.launch({channel,headless:true});
const base='http://127.0.0.1:3118', actor='9007199254740993',target='9007199254740994';
const checks=[];
const current={userId:actor,grantVersion:'1',status:'active',memberships:[],grants:[],allowedActions:['invite.manage','membership.propose','membership.approve','access.approve','access.audit.read','recovery.initiate'],scopes:[
{id:'10',kind:'buyingOrganization',parentId:null,status:'active',version:'1'},
{id:'11',kind:'branch',parentId:'10',status:'active',version:'1'},
{id:'12',kind:'division',parentId:'11',status:'active',version:'1'},
{id:'100',kind:'fulfillmentOrganization',parentId:null,status:'active',version:'1'},
{id:'101',kind:'shop',parentId:'100',status:'active',version:'1'},
{id:'103',kind:'channel',parentId:'100',status:'active',version:'1'}]};
const catalog={allowedActions:current.allowedActions,roles:[
 {id:'buyer',label:'Pembeli',version:1,permissions:['catalog.read','order.read'],suggestedPermissions:['substitution.accept']},
 {id:'store-staff',label:'Staf toko',version:1,permissions:['catalog.read','stock.read'],suggestedPermissions:['substitution.propose','stock.convert','reservation.release','supplier.change.propose']}
],financePacks:[],permissions:[
 {id:'catalog.read',label:'Lihat katalog',allowedScopes:['buyerOwned','fulfillmentShop'],requiresFreshMfa:false},
 {id:'order.read',label:'Lihat pesanan',allowedScopes:['buyerOwned'],requiresFreshMfa:false},
 {id:'stock.read',label:'Lihat stok',allowedScopes:['fulfillmentShop'],requiresFreshMfa:false},
 {id:'substitution.accept',label:'substitution.accept',allowedScopes:['buyerOwned'],requiresFreshMfa:false},
 ...['substitution.propose','stock.convert','reservation.release','supplier.change.propose'].map(id=>({id,label:id,allowedScopes:['fulfillmentShop'],requiresFreshMfa:false}))
]};
const invitation={id:'9007199254740995',userId:target,intendedEmail:'invite@example.test',scope:{kind:'buyerOwned',organizationId:'10',branchId:'11'},role:null,requestedBy:'80',approvedBy:null,reason:'Account for approved BNI branch',expiresAt:new Date(Date.now()+86400000).toISOString(),accessExpiresAt:null,version:'9007199254740996',status:'pending',allowedActions:['approve','reject']};
const profile={userId:target,displayName:'Operator Cabang',verifiedEmail:'operator@example.test',verifiedAt:new Date().toISOString(),status:'active',securityVersion:'9007199254740997',grantVersion:'1'};
async function setup(width=1365,height=1000){
 const context=await browser.newContext({viewport:{width,height},locale:'id-ID',timezoneId:'Asia/Jakarta',serviceWorkers:'block'});
 const page=await context.newPage(); page.setDefaultTimeout(10000);
 const state={mode:'ok',decision:'ok',proposal:'ok',receipt:'committed',actorId:actor,calls:[],errors:[],reads:0};
 page.on('pageerror',e=>state.errors.push(e.message));
 page.on('dialog',dialog=>dialog.accept());
 await page.route('**/*',async route=>{
  const request=route.request(),url=new URL(request.url());
  if(url.origin!==base)return route.abort();
  const answer=(body,status=200)=>route.fulfill({status,contentType:'application/json',headers:{'Cache-Control':'no-store'},body:JSON.stringify(body)});
  // The page needs a session cookie to render (the proxy sends cookie-less visits to sign-in); the API stays mocked.
  if(!url.pathname.startsWith('/api/v1'))return route.continue({headers:{...request.headers(),cookie:'__Host-ut_session=synthetic-check'}});
  if(url.pathname==='/api/v1/identity/csrf')return answer({requestToken:'fixture-csrf'});
  if(url.pathname==='/api/v1/access/current'){
   if(state.mode==='unauthenticated')return answer({code:'unauthenticated'},401);
   return answer({...current,userId:state.actorId,...(state.mode==='forbidden'?{allowedActions:[]}: {})});
  }
  assert.equal(request.headers()['x-expected-actor-id'],state.actorId);
  if(url.pathname==='/api/v1/access/catalog')return answer(catalog);
  assert.equal(request.method(),'POST');
  assert.equal(request.headers()['x-csrf-token'],'fixture-csrf');
  assert.equal(url.search,'');
  const body=request.postDataJSON();
  if(url.pathname.endsWith('/directory/search')||url.pathname.endsWith('/directory/query')){
   state.reads++;return answer({total:'1',items:[profile]});
  }
  if(url.pathname.endsWith('/invitations/query'))return answer({total:'1',items:[invitation]});
  if(url.pathname==='/api/v1/access/operations/lookup'){
   if(state.receipt==='actor_changed')return answer({code:'actor_changed'},409);
   if(state.receipt==='unauthenticated')return answer({code:'unauthenticated'},401);
   return answer({state:'committed',result:{id:invitation.id,userId:target,version:'1',status:'pending'}});
  }
  const call={path:url.pathname,body,key:request.headers()['idempotency-key'],version:request.headers()['if-match']};
  assert.match(call.key,/^[A-Za-z0-9._:-]+$/);state.calls.push(call);
  if(url.pathname.endsWith('/invitations')){
   if(state.proposal==='uncertain')return answer({code:'commit_outcome_unknown',traceId:'fixture'},503);
   return answer({id:invitation.id,userId:target,version:'1',status:'pending'});
  }
  if(url.pathname.endsWith('/approve')){
   assert.equal(call.version,'"'+invitation.version+'"');
   if(state.decision==='stale')return answer({code:'version_conflict'},409);
   return answer({id:invitation.id,userId:target,version:'9007199254740997',status:'approved'});
  }
  if(url.pathname.endsWith('/recovery')){
   assert.equal(call.version,'"'+profile.securityVersion+'"');
   return answer({id:'100',userId:target,securityVersion:'9007199254740998',status:'blocked'});
  }
  throw new Error('Unexpected API '+url.pathname);
 });
 async function open(){await page.goto(base+'/administration/accounts');await page.getByRole('heading',{name:'Akun dan undangan',exact:true}).waitFor();if(state.mode==='ok'){await page.getByText(profile.verifiedEmail,{exact:true}).waitFor();await page.getByLabel('Alamat email penerima',{exact:true}).waitFor();}}
 await open();return {page,context,state,open};
}
async function proposal(page){
 await page.getByLabel('Alamat email penerima',{exact:true}).fill('new@example.test');
 await page.getByRole('combobox',{name:/Jenis lingkup/}).selectOption('buyerOwned');
 const select=page.getByRole('combobox',{name:/Organisasi dan unit yang dituju/});
 await select.selectOption({index:2});
 const tomorrow=new Date(Date.now()+86400000+7*3600000).toISOString().slice(0,16);
 await page.getByLabel('Undangan berakhir',{exact:false}).fill(tomorrow);
 await page.getByLabel('Alasan pengundangan',{exact:true}).fill('BNI branch account approved for onboarding');
 await page.getByRole('button',{name:'Tinjau usulan',exact:true}).click();
 await page.getByRole('button',{name:'Kirim usulan undangan',exact:true}).waitFor();
}
async function run(name,action){
 try{await action();checks.push({name,passed:true});console.log('PASS '+name);}
 catch(error){checks.push({name,passed:false,error:error.message});console.log('FAIL '+name+': '+error.message);}
}
try{
 if(!process.argv.includes('--acc02')){
 if(!process.argv.includes('--regressions')){
 for(const [name,w,h] of [['desktop',1365,1000],['mobile',390,844]])await run(name+' administration flow',async()=>{
  const {page,context,state}=await setup(w,h);
  try{
   const search=page.getByRole('searchbox',{name:'Cari akun'});
   assert.equal(await search.getAttribute('minlength'),'2');
   assert.equal(await search.getAttribute('maxlength'),'80');
   const priorReads=state.reads;
   await search.fill('O');
   assert.equal(await search.evaluate(input=>input.validity.tooShort),true);
   await page.getByRole('button',{name:'Cari',exact:true}).click();
   await page.waitForTimeout(100);
   assert.equal(state.reads,priorReads,'one-character search must not request the directory');
   await search.fill('Operator');
   const searchResponse=page.waitForResponse(response=>response.url().endsWith('/api/v1/identity/administration/directory/search'));
   await page.getByRole('button',{name:'Cari',exact:true}).click();
   await searchResponse;
   assert.equal(state.reads,priorReads+1,'valid search must request the directory once');
   await page.getByText(profile.verifiedEmail,{exact:true}).waitFor();
   await proposal(page);
   await page.getByLabel('Alasan pengundangan',{exact:true}).fill('Revised approved onboarding reason');
   assert.equal(await page.getByRole('button',{name:'Kirim usulan undangan',exact:true}).count(),0,'edits invalidate review');
   await page.getByRole('button',{name:'Tinjau usulan',exact:true}).click();
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true,'no horizontal overflow');
   await page.screenshot({path:outputFile('accounts-'+name+'.png'),fullPage:true});
   await page.getByRole('button',{name:'Kirim usulan undangan',exact:true}).click();
   await page.getByRole('status').filter({hasText:'Pengajuan undangan tersimpan'}).waitFor();
   assert.equal(await page.getByLabel('Alamat email penerima',{exact:true}).inputValue(),'');
   assert.equal(state.calls.length,1);
   await page.getByRole('button',{name:'Setujui undangan '+invitation.id,exact:true}).click();
   const heading=page.getByRole('heading',{name:'Periksa keputusan undangan',exact:true});
   await heading.waitFor();assert.equal(await heading.evaluate(el=>document.activeElement===el),true,'review receives keyboard focus');
   await page.getByLabel('Alasan keputusan',{exact:true}).fill('Independent review completed');
   state.decision='stale';
   await page.getByRole('button',{name:'Konfirmasi persetujuan',exact:true}).click();
   await page.getByRole('alert').filter({hasText:'Data atau izin telah berubah'}).waitFor();
   assert.equal(await heading.count(),0,'stale review must close');
   state.decision='ok';
   await page.getByRole('button',{name:'Setujui undangan '+invitation.id,exact:true}).click();
   await page.getByLabel('Alasan keputusan',{exact:true}).fill('Independent fresh review');
   await page.getByRole('button',{name:'Konfirmasi persetujuan',exact:true}).click();
   await page.getByRole('status').filter({hasText:'Pengiriman melalui penyedia identitas masih tertahan'}).waitFor();
   await page.getByRole('button',{name:'Tinjau pemulihan Operator Cabang',exact:true}).click();
   await page.getByRole('combobox',{name:/Jenis pemulihan/}).selectOption('factor');
   await page.getByLabel('Alasan keputusan',{exact:true}).fill('Owner recovery support requested');
   await page.getByRole('button',{name:'Cabut sesi dan catat pemulihan',exact:true}).click();
   await page.getByRole('status').filter({hasText:'Pemulihan di penyedia belum selesai'}).waitFor();
   assert.equal(state.calls.at(-1).body.kind,'factor');
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
   assert.deepEqual(state.errors,[]);
  }finally{await context.close();}
 });
 }
 for(const action of ['retry','receipt'])await run('uncertain proposal clears reviewed form after '+action,async()=>{
  const {page,context,state}=await setup();
  try{
   await proposal(page);state.proposal='uncertain';
   await page.getByRole('button',{name:'Kirim usulan undangan',exact:true}).click();
   await page.getByRole('button',{name:'Periksa ulang permintaan yang sama',exact:true}).waitFor();
   assert.equal(state.calls.length,1);
   const journal=await page.evaluate(()=>Object.entries(localStorage).filter(([key])=>key.startsWith('unit-toko:pending:v1:')));
   assert.equal(journal.length,1);assert.equal(JSON.stringify(journal).includes('new@example.test'),false);
   assert.equal(JSON.stringify(journal).includes('BNI branch'),false);
   if(action==='retry'){
    state.proposal='ok';await page.getByRole('button',{name:'Periksa ulang permintaan yang sama',exact:true}).click();
    await page.getByRole('status').filter({hasText:'Pengajuan undangan tersimpan'}).waitFor();
    assert.equal(state.calls.length,2);assert.deepEqual(state.calls[0],state.calls[1],'retry unchanged key and body');
   }else{
    await page.getByRole('button',{name:'Periksa hasil',exact:true}).click();
    await page.getByRole('status').filter({hasText:'Permintaan telah tersimpan'}).waitFor();
   }
   assert.equal(await page.getByLabel('Alamat email penerima',{exact:true}).inputValue(),'','completed uncertain proposal must clear');
   assert.equal(await page.getByRole('button',{name:'Kirim usulan undangan',exact:true}).count(),0,'no duplicate reviewed resubmission');
  }finally{await context.close();}
 });
 for(const mode of ['actor_changed','unauthenticated'])await run('receipt '+mode+' removes prior account data',async()=>{
  const {page,context,state}=await setup();
  try{
   await proposal(page);state.proposal='uncertain';
   await page.getByRole('button',{name:'Kirim usulan undangan',exact:true}).click();
   await page.getByRole('button',{name:'Periksa hasil',exact:true}).waitFor();
   state.receipt=mode;
   await page.getByRole('button',{name:'Periksa hasil',exact:true}).click();
   await page.getByRole('heading',{name:'Sesi Anda perlu diperbarui',exact:true}).waitFor();
   assert.equal(await page.getByText(profile.verifiedEmail,{exact:true}).count(),0,'PII must clear on receipt auth failure');
   assert.equal(await page.getByLabel('Alamat email penerima',{exact:true}).count(),0);
  }finally{await context.close();}
 });
 if(!process.argv.includes('--regressions')){
 await run('reload restores only reference and reconciles',async()=>{
  const {page,context,state}=await setup();
  try{
   await proposal(page);state.proposal='uncertain';await page.getByRole('button',{name:'Kirim usulan undangan',exact:true}).click();
   await page.getByRole('button',{name:'Periksa hasil',exact:true}).waitFor();
   await page.reload();await page.getByRole('button',{name:'Periksa hasil',exact:true}).waitFor();
   assert.equal(await page.getByLabel('Alamat email penerima',{exact:true}).inputValue(),'');
   assert.equal(await page.getByLabel('Alamat email penerima',{exact:true}).isDisabled(),true);
   await page.getByRole('button',{name:'Periksa hasil',exact:true}).click();await page.getByRole('status').filter({hasText:'Permintaan telah tersimpan'}).waitFor();
   assert.equal(await page.getByLabel('Alamat email penerima',{exact:true}).isDisabled(),false);
   assert.equal(state.calls.length,1,'reload does not automatically resubmit');
  }finally{await context.close();}
 });
 for(const mode of ['unauthenticated','forbidden'])await run(mode+' route state',async()=>{
  const {page,context,state,open}=await setup();
  try{state.mode=mode;await open();await page.getByRole('heading',{name:mode==='unauthenticated'?'Sesi Anda perlu diperbarui':'Halaman ini di luar tugas akun Anda',exact:true}).waitFor();assert.equal(await page.getByText(profile.verifiedEmail,{exact:true}).count(),0);}
  finally{await context.close();}
 });
 }
 }
 if(!process.argv.includes('--regressions')){
 for(const [label,width,height] of [['desktop',1365,1000],['mobile',390,844]])await run('ACC02 explicit suggestions and draft reset '+label,async()=>{
  const {page,context,state}=await setup(width,height);
  try{
   await proposal(page);
   await page.getByRole('radio',{name:'Pembeli',exact:true}).check();
   const accept=page.getByRole('checkbox',{name:'Setujui penggantian barang',exact:true});
   assert.equal(await accept.isChecked(),false,'a suggestion must never be preselected');
   await page.getByRole('button',{name:'Tinjau usulan',exact:true}).click();
   const review=page.getByRole('region',{name:'Tinjauan undangan'});
   assert.equal(await review.getByText('Setujui penggantian barang',{exact:false}).count(),0);
   await accept.check();
   assert.equal(await page.getByRole('button',{name:'Kirim usulan undangan',exact:true}).count(),0,'permission edit invalidates review');
   await page.getByRole('button',{name:'Tinjau usulan',exact:true}).click();
   await review.getByText('Setujui penggantian barang',{exact:false}).waitFor();
   await page.getByLabel('Alamat email penerima',{exact:true}).fill('other.recipient@example.test');
   assert.equal(await accept.isChecked(),false,'recipient change clears selected extras');
   assert.equal(await page.getByRole('button',{name:'Kirim usulan undangan',exact:true}).count(),0);
   await accept.check();
   const scopeSelect=page.getByRole('combobox',{name:/Organisasi dan unit yang dituju/});
   await scopeSelect.selectOption({index:1});
   await page.getByRole('radio',{name:'Pembeli',exact:true}).check();
   assert.equal(await accept.isChecked(),false,'scope change clears selected extras');
   await page.getByRole('combobox',{name:/Jenis lingkup/}).selectOption('fulfillmentShop');
   await scopeSelect.selectOption({index:1});
   await page.getByRole('radio',{name:'Staf toko',exact:true}).check();
   assert.equal(await accept.count(),0,'buyer consent is not offered on a fulfillment scope');
   const convert=page.getByRole('checkbox',{name:'Konversi kemasan stok',exact:true});
   const release=page.getByRole('checkbox',{name:'Lepaskan reservasi stok',exact:true});
   assert.equal(await convert.isChecked(),false);
   await convert.check();
   await page.getByRole('radio',{name:'Keanggotaan saja, tanpa peran',exact:true}).check();
   await page.getByRole('radio',{name:'Staf toko',exact:true}).check();
   assert.equal(await convert.isChecked(),false,'role change clears selected extras');
   await convert.check();await release.check();
   await page.getByRole('button',{name:'Tinjau usulan',exact:true}).click();
   await review.getByText('Konversi kemasan stok',{exact:false}).waitFor();
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true,'suggestions fit viewport');
   await page.screenshot({path:outputFile('accounts-acc02-'+label+'.png'),fullPage:true});
   await page.getByRole('button',{name:'Kirim usulan undangan',exact:true}).click();
   await page.getByRole('status').filter({hasText:'Pengajuan undangan tersimpan'}).waitFor();
   assert.equal(state.calls.length,1);
   assert.deepEqual(state.calls[0].body.role,{templateId:'store-staff',financePacks:[],additionalPermissions:['reservation.release','stock.convert'],delegablePermissions:[]});
   assert.equal(state.calls[0].body.email,'other.recipient@example.test');
   assert.deepEqual(state.errors,[]);
  }finally{await context.close();}
 });
 await run('ACC02 account change clears selected suggestions and review',async()=>{
  const {page,context,state}=await setup();
  try{
   await proposal(page);
   await page.getByRole('radio',{name:'Pembeli',exact:true}).check();
   await page.getByRole('checkbox',{name:'Setujui penggantian barang',exact:true}).check();
   await page.getByRole('button',{name:'Tinjau usulan',exact:true}).click();
   state.actorId='9007199254740998';
   await page.getByRole('button',{name:'Muat ulang akses',exact:true}).click();
   await page.getByText('Akun yang memeriksa: '+state.actorId,{exact:true}).waitFor();
   assert.equal(await page.getByLabel('Alamat email penerima',{exact:true}).inputValue(),'');
   assert.equal(await page.getByRole('checkbox',{name:'Setujui penggantian barang',exact:true}).count(),0);
   assert.equal(await page.getByRole('button',{name:'Kirim usulan undangan',exact:true}).count(),0);
   assert.equal(state.calls.length,0);
  }finally{await context.close();}
 });
 }
}finally{await browser.close();}
const report={at:new Date().toISOString(),scope:'Real browser UI with synthetic API responses only, not full stack or Auth0 acceptance',checks};
await writeFile(outputFile(process.argv.includes('--acc02')?'accounts-browser-acc02.json':process.argv.includes('--regressions')?'accounts-browser-regressions.json':'accounts-browser-result.json'),JSON.stringify(report,null,2));
if(checks.some(check=>!check.passed))process.exitCode=1;
