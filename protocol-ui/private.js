import { KeyHelper, SignalProtocolAddress, SessionBuilder, SessionCipher } from '@privacyresearch/libsignal-protocol-typescript';

const b64=b=>btoa(String.fromCharCode(...new Uint8Array(b)));
const bytes=s=>Uint8Array.from(atob(s),c=>c.charCodeAt(0)).buffer;
const pack=p=>({pubKey:b64(p.pubKey),privKey:b64(p.privKey)});
const unpack=p=>p?{pubKey:bytes(p.pubKey),privKey:bytes(p.privKey)}:undefined;
const fresh=()=>1+crypto.getRandomValues(new Uint32Array(1))[0]%1073741822;
let context=null;
let browserDb;
function database(){
  return browserDb ||= new Promise((resolve,reject)=>{
    const req=indexedDB.open('ctrl-device-secrets',1);
    req.onupgradeneeded=()=>req.result.createObjectStore('rows');
    req.onsuccess=()=>resolve(req.result);req.onerror=()=>reject(req.error);
  });
}
function result(req){return new Promise((res,rej)=>{req.onsuccess=()=>res(req.result);req.onerror=()=>rej(req.error);});}
async function browserStore(scope,query){
  const db=await database();
  const read=key=>result(db.transaction('rows').objectStore('rows').get(key));
  let key=await read('key');
  if(!key){
    const next=await crypto.subtle.generateKey({name:'AES-GCM',length:256},false,['encrypt','decrypt']);
    try{await result(db.transaction('rows','readwrite').objectStore('rows').add(next,'key'));}catch{}
    key=await read('key');
  }
  const prefix=scope+'|';
  if(query.key){
    const item=await read(prefix+query.key);if(!item)return null;
    const data=await crypto.subtle.decrypt({name:'AES-GCM',iv:item.iv,additionalData:new TextEncoder().encode(prefix+query.key)},key,item.data);
    return JSON.parse(new TextDecoder().decode(data));
  }
  const records=await Promise.all(Object.entries(query.changes).map(async([name,value])=>{
    if(value===null)return [prefix+name,null];
    const iv=crypto.getRandomValues(new Uint8Array(12));
    const data=await crypto.subtle.encrypt({name:'AES-GCM',iv,additionalData:new TextEncoder().encode(prefix+name)},key,new TextEncoder().encode(JSON.stringify(value)));
    return [prefix+name,{iv,data}];
  }));
  const tx=db.transaction('rows','readwrite'),rows=tx.objectStore('rows');
  for(const [name,value] of records)value===null?rows.delete(name):rows.put(value,name);
  await new Promise((res,rej)=>{tx.oncomplete=res;tx.onabort=()=>rej(tx.error);});
}
function backend(scope,args){
  if(window.CtrlMobile)return window.CtrlMobile.request({action:'vault',scope,...args}).then(r=>r.value);
  return CtrlLive.native?CtrlLive.bridge({action:'vault',scope,...args}).then(r=>r.value):browserStore(scope,args);
}
async function locked(ctx,fn){
  return navigator.locks.request('ctrl-private-'+ctx.uid,async()=>{
    const changes=Object.create(null);
    const get=async k=>Object.hasOwn(changes,k)?changes[k]:backend(ctx.uid,{key:k});
    const put=(k,v)=>{changes[k]=v;};
    const store={
      getIdentityKeyPair:async()=>unpack((await get('id')).pair), getLocalRegistrationId:async()=>(await get('id')).registration,
      isTrustedIdentity:async(a,k)=>{const pinned=await get('p:'+a);return !pinned||pinned===b64(k);},
      saveIdentity:async(a,k)=>{const old=await get('p:'+a);put('p:'+a,b64(k));return !!old&&old!==b64(k);},
      loadPreKey:async k=>unpack(await get('k:'+k)), storePreKey:async(k,v)=>put('k:'+k,pack(v)),removePreKey:async k=>put('k:'+k,null),
      loadSignedPreKey:async k=>unpack(await get('signed:'+k)),storeSignedPreKey:async(k,v)=>put('signed:'+k,pack(v)),removeSignedPreKey:async k=>put('signed:'+k,null),
      storeSession:async(a,v)=>put('s:'+a,v),loadSession:async a=>(await get('s:'+a))??undefined
    };
    const answer=await fn({get,put,store});
    if(Object.keys(changes).length)await backend(ctx.uid,{changes});
    return answer;
  });
}
async function start(uid){
  const ctx={uid,device:0};context=ctx;
  const bundle=await locked(ctx,async({get,put})=>{
    const existing=await get('id');
    if(existing)return existing.bundle;
    const pair=await KeyHelper.generateIdentityKeyPair(),device=fresh(),registration=KeyHelper.generateRegistrationId() || 1;
    const signed=await KeyHelper.generateSignedPreKey(pair,fresh());put('signed:'+signed.keyId,pack(signed.keyPair));
    const keys=[];
    for(let i=0;i<32;i++){const pre=await KeyHelper.generatePreKey(fresh());put('k:'+pre.keyId,pack(pre.keyPair));keys.push({id:pre.keyId,key:b64(pre.keyPair.pubKey)});}
    const pub={identity:b64(pair.pubKey),registration,device,signedId:signed.keyId,signedKey:b64(signed.keyPair.pubKey),signature:b64(signed.signature),keys};
    put('id',{pair:pack(pair),registration,bundle:pub});return pub;
  });
  const {available}=await CtrlLive.api('/signal/device','POST',bundle);
  if(available<10){
    const freshKeys=await locked(ctx,async({get,put})=>{
      const keys=[];for(let i=0;i<32;i++){const k=await KeyHelper.generatePreKey(fresh());put('k:'+k.keyId,pack(k.keyPair));keys.push({id:k.keyId,key:b64(k.keyPair.pubKey)});}
      const id=await get('id');id.bundle={...id.bundle,keys};put('id',id);return id.bundle;
    });
    await CtrlLive.api('/signal/device','POST',freshKeys);
  }
  ctx.device=bundle.device;
}
async function seal(chat,peer,text,edit=0){
  const ctx=context;if(!ctx?.device)throw Error('Хранилище сообщений ещё не готово.');
  return locked(ctx,async({get,put,store})=>{
    const old=await get('pending:'+chat);
    if(old && old.text===text && old.edit===edit)return old;
    const devices=await CtrlLive.api('/signal/chats/'+chat+'/devices');
    if(peer&&!devices.some(d=>d.uid===peer))throw Error('Собеседнику нужно открыть обновлённый Ctrl, чтобы получить сообщение.');
    const event=crypto.randomUUID(), boxes=[];
    const message=new TextEncoder().encode(JSON.stringify({chat,event,text})).buffer;
    for(const d of devices){
      if(d.uid===ctx.uid && d.device===ctx.device)continue;
      const address=new SignalProtocolAddress(d.uid,d.device);
      const cipher=new SessionCipher(store,address);
      if(!await cipher.hasOpenSession()){
        const {preKey}=await CtrlLive.api('/signal/chats/'+chat+'/key','POST',{uid:d.uid,device:d.device});
        await new SessionBuilder(store,address).processPreKey({identityKey:bytes(d.identity),registrationId:d.registration,
          signedPreKey:{keyId:d.signedId,publicKey:bytes(d.signedKey),signature:bytes(d.signature)},
          ...(preKey?{preKey:{keyId:preKey.id,publicKey:bytes(preKey.key)}}:{})});
      }
      const enc=await cipher.encrypt(message);boxes.push({uid:d.uid,device:d.device,type:enc.type,body:btoa(enc.body)});
    }
    const pending={clientId:event,text,edit,body:'SIG1:'+JSON.stringify({from:ctx.device,event,boxes})};
    put('pending:'+chat,pending);put('history:'+chat+':'+event,text);
    return pending;
  });
}
async function opened(chat,msg){
  if(!msg.body.startsWith('SIG1:')) return msg.body;
  const ctx=context;if(!ctx?.device)return 'Сообщение на другом устройстве';
  try{
    const envelope=JSON.parse(msg.body.slice(5));
    return await locked(ctx,async({get,put,store})=>{
      const key='history:'+chat+':'+envelope.event;
      const cached=await get(key);if(cached!=null)return cached;
      const mine=envelope.boxes.find(b=>b.uid===ctx.uid&&b.device===ctx.device);if(!mine)return 'Сообщение на другом устройстве';
      const cipher=new SessionCipher(store,new SignalProtocolAddress(msg.sender,envelope.from));
      const output=mine.type===3?await cipher.decryptPreKeyWhisperMessage(bytes(mine.body)):await cipher.decryptWhisperMessage(bytes(mine.body));
      const decoded=JSON.parse(new TextDecoder().decode(output));
      if(decoded.chat!==chat||decoded.event!==envelope.event||typeof decoded.text!=='string')throw Error('Invalid content');
      put(key,decoded.text);return decoded.text;
    });
  }catch{return 'Не удалось прочитать сообщение';}
}
async function sent(chat,id){const ctx=context;if(!ctx)return;await locked(ctx,async({get,put})=>{if((await get('pending:'+chat))?.clientId===id)put('pending:'+chat,null);});}
window.CtrlPrivate={start,seal,opened,sent,close:()=>{context=null;}};

