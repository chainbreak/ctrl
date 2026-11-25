import assert from 'node:assert/strict';
import {DatabaseSync} from 'node:sqlite';
import {mkdtemp} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join,resolve} from 'node:path';
import {pbkdf2Sync,randomBytes,randomUUID,createHmac} from 'node:crypto';
import {createServer} from 'node:net';
import {once} from 'node:events';
import {spawn} from 'node:child_process';
const temp=await mkdtemp(join(tmpdir(),'ctrl-staff-'));
async function origin(){const s=createServer();s.listen(0,'127.0.0.1');await once(s,'listening');const u='http://127.0.0.1:'+s.address().port;await new Promise(r=>s.close(r));return u;}
const service=await origin(),desk=await origin(),secret=randomBytes(32).toString('hex');
const source=resolve('../backend/Ctrl.Api'),file=join(temp,'staff.db');
const env={...process.env,CTRL_MANAGEMENT_KEY:secret,CTRL_MESSENGER_ORIGIN:service};
const server=spawn(resolve('publish-server/ctrl-server.exe'),[],{stdio:'ignore',windowsHide:true,env:{...env,CTRL_DATA:join(temp,'messenger'),CTRL_LISTEN:service}});
const portal=spawn('dotnet',[join(source,'bin/Debug/net10.0/Ctrl.Api.dll')],{cwd:source,windowsHide:true,stdio:'ignore',env:{...env,ASPNETCORE_ENVIRONMENT:'Development',ASPNETCORE_URLS:desk,ConnectionStrings__Default:'Data Source='+file}});
const wait=async url=>{for(let i=0;i<100;i++){if(await fetch(url).then(r=>r.ok).catch(()=>false))return;await new Promise(r=>setTimeout(r,150));}throw Error('Service startup timeout');};
let db;
try{
  await Promise.all([wait(desk+'/api/auth/csrf'),wait(service+'/health')]);db=new DatabaseSync(file);
  const uid=randomUUID(),role=randomUUID(),email='staff-fixture@example.org',pass=randomBytes(18).toString('hex');
  const salt=randomBytes(16),header=Buffer.alloc(13);header[0]=1;header.writeUInt32BE(2,1);header.writeUInt32BE(100000,5);header.writeUInt32BE(16,9);
  const hash=Buffer.concat([header,salt,pbkdf2Sync(pass,salt,100000,32,'sha512')]).toString('base64');
  db.prepare('INSERT INTO AspNetUsers(Id,UserName,NormalizedUserName,Email,NormalizedEmail,EmailConfirmed,PasswordHash,SecurityStamp,ConcurrencyStamp,PhoneNumberConfirmed,TwoFactorEnabled,LockoutEnabled,AccessFailedCount,Bio,IsBanned,ClicksBalance) VALUES(?,?,?,?,?,1,?,?,?,0,0,1,0,?,0,0)').run(uid,email,email.toUpperCase(),email,email.toUpperCase(),hash,randomUUID(),randomUUID(),'');
  db.prepare('INSERT INTO AspNetRoles(Id,Name,NormalizedName,ConcurrencyStamp) VALUES(?,?,?,?)').run(role,'Support','SUPPORT',randomUUID());
  db.prepare('INSERT INTO AspNetUserRoles VALUES(?,?)').run(uid,role);
  const cookies=new Map();let csrf='';
  async function call(path,body,status=200){
    const res=await fetch(desk+'/api'+path,{method:body?'POST':'GET',headers:{Cookie:[...cookies].map(([k,v])=>k+'='+v).join('; '),'Content-Type':'application/json','X-CSRF-TOKEN':csrf},body:body?JSON.stringify(body):undefined});
    for(const value of res.headers.getSetCookie()){const first=value.split(';')[0],i=first.indexOf('=');cookies.set(first.slice(0,i),first.slice(i+1));}
    assert.equal(res.status,status,path);return res.json().catch(()=>null);
  }
  async function renew(){csrf=(await call('/auth/csrf')).requestToken;}
  await renew();await call('/auth/login',{email,password:pass});await renew();
  await call('/admin/messenger',{kind:'queue'},403);
  const setup=await call('/auth/two-factor/setup',{});const bits=[...setup.sharedKey.toUpperCase()].map(x=>'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'.indexOf(x).toString(2).padStart(5,'0')).join('');
  const key=Buffer.from(bits.match(/.{8}/g).map(x=>parseInt(x,2)));
  function code(){const count=Buffer.alloc(8);count.writeBigUInt64BE(BigInt(Math.floor(Date.now()/30000)));const mac=createHmac('sha1',key).update(count).digest(),n=mac[19]&15;return String((mac.readUInt32BE(n)&0x7fffffff)%1000000).padStart(6,'0');}
  await call('/auth/two-factor/enable',{code:code()});await renew();
  await call('/auth/login',{email,password:pass},202);await renew();await call('/auth/login/two-factor',{code:code()});await renew();
  const queue=await call('/admin/messenger',{kind:'queue'});assert.ok(Array.isArray(queue.noer));
  await call('/admin/messenger',{kind:'staff-handle',target:'none',note:'ceo',operation:randomUUID()},403);
  console.log('PASS staff enrollment, MFA cookie, CSRF-protected support gateway and role boundary');
}finally{db?.close();portal.kill();server.kill();}

