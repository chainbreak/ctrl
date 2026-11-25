import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {mkdtemp,mkdir,readFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join,resolve} from 'node:path';
import {randomBytes,randomUUID,createHmac} from 'node:crypto';
import {DatabaseSync} from 'node:sqlite';
import {createServer} from 'node:net';
import {once} from 'node:events';
const temp=await mkdtemp(join(tmpdir(),'ctrl-services-'));
const reserve=createServer();reserve.listen(0,'127.0.0.1');await once(reserve,'listening');const base='http://127.0.0.1:'+reserve.address().port;await new Promise(r=>reserve.close(r));
const secret=randomBytes(32).toString('hex'),key=randomBytes(32).toString('hex');
const exe=spawn(resolve('publish-server/ctrl-server.exe'),[],{windowsHide:true,stdio:'ignore',env:{...process.env,CTRL_DATA:temp,CTRL_LISTEN:base,CTRL_MANAGEMENT_KEY:key,CTRL_STRIPE_SECRET:'qa_fixture_no_external_requests',CTRL_STRIPE_WEBHOOK_SECRET:secret,CTRL_STRIPE_RETURN_ORIGIN:'https://ctrl.example.org',CTRL_STRIPE_CLICK_UNITS:'25',CTRL_STRIPE_CLICK_CENTS:'100'}});
const json=async(path,body,token,expected=200,extra={})=>{
  const r=await fetch(base+path,{method:body?'POST':'GET',headers:{'Content-Type':'application/json',...(token?{Authorization:'Bearer '+token}:{}),...extra},body:body?JSON.stringify(body):undefined});
  assert.equal(r.status,expected,path);return r.json().catch(()=>null);
};
let db;
try{
  for(let i=0;i<100;i++){try{await json('/health');break;}catch{await new Promise(r=>setTimeout(r,150));}}
  const user=await json('/api/register',{email:'qa_ctrl_services@gmail.com',name:'QA',username:'ctrltester',password:'Test-only-password24'});
  db=new DatabaseSync(join(temp,'ctrl.db'));db.exec('PRAGMA busy_timeout=5000');
  const staff=(kind,target,amount=0)=>json('/management',{kind,target,amount,actor:'operator',operation:randomUUID()},null,200,{'X-Ctrl-Management':key});
  for(let i=0;i<10;i++)await json('/api/community/rooms',{title:'Канал '+i,kind:'channel'},user.token);
  await json('/api/community/rooms',{title:'Канал 11',kind:'channel'},user.token,409);
  await staff('premium',user.user.id,1);for(let i=10;i<20;i++)await json('/api/community/rooms',{title:'Канал '+i,kind:'channel'},user.token);
  await json('/api/community/rooms',{title:'21',kind:'channel'},user.token,409);
  const noer=await json('/api/noer',{document:Buffer.from('%PDF-1.6 synthetic').toString('base64')},user.token);
  const paid=async(kind,reference,cents,units)=>{
    const id=randomUUID().replaceAll('-','');db.prepare('INSERT INTO checkout_orders(id,uid,kind,ref,cents,units) VALUES(?,?,?,?,?,?)').run(id,user.user.id,kind,reference,cents,units);
    const evt={type:'checkout.session.completed',data:{object:{payment_status:'paid',mode:'payment',client_reference_id:id,currency:'usd',amount_total:cents}}};
    const ts=Math.floor(Date.now()/1000),sign=createHmac('sha256',secret).update(ts+'.'+JSON.stringify(evt)).digest('hex');
    await json('/payments/stripe',evt,null,400,{'Stripe-Signature':'t='+ts+',v1='+'0'.repeat(64)});
    for(let i=0;i<2;i++)await json('/payments/stripe',evt,null,200,{'Stripe-Signature':'t='+ts+',v1='+sign});
  };
  await paid('noer',noer.id,10000,1);await staff('approve-noer',noer.id);assert.equal((await json('/api/me',null,user.token)).verified,1);
  await paid('clicks','',100,25);assert.equal((await json('/api/me',null,user.token)).clicks,25);
  const art='data:image/gif;base64,'+Buffer.from('GIF89a00000000000000').toString('base64');
  await json('/api/me/artwork',{field:'profileBanner',data:art},user.token);
  await staff('premium',user.user.id,0);
  await json('/api/me/artwork',{field:'profileBanner',data:art},user.token,403);
  const peer=await json('/api/register',{email:'ctrl_qa_second@gmail.com',name:'Second',username:'ctrlsecond',password:'Test-only-password24'});
  const c=await json('/api/conversations',{peerId:peer.user.id},user.token);
  await json('/api/files',{chat:c.id,size:700000001},user.token,413);await json('/api/files',{chat:c.id,size:700000000},user.token);
  await staff('premium',user.user.id,1);await json('/api/files',{chat:c.id,size:2000000000},user.token);await json('/api/files',{chat:c.id,size:2000000001},user.token,413);
  await staff('ban',user.user.id,1);await json('/api/me',null,user.token,401);
  await json('/api/login',{loginName:'ctrltester',password:'Test-only-password24'},null,403);
  console.log('PASS channel/file quota boundaries, staff ban, signed payment webhook replay, NOER and Premium artwork');
}finally{db?.close();exe.kill();}

