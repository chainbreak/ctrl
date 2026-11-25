const CtrlInstall=(()=>{
  async function webKey(){
    const db=await new Promise((res,rej)=>{const r=indexedDB.open('ctrl-install',1);r.onupgradeneeded=()=>r.result.createObjectStore('identity');r.onsuccess=()=>res(r.result);r.onerror=()=>rej(r.error);});
    const req=(r)=>new Promise((res,rej)=>{r.onsuccess=()=>res(r.result);r.onerror=()=>rej(r.error);});
    try{
      let k=await req(db.transaction('identity').objectStore('identity').get('key'));if(k)return k;
      k=await crypto.subtle.generateKey({name:'ECDSA',namedCurve:'P-256'},false,['sign','verify']);
      try{await req(db.transaction('identity','readwrite').objectStore('identity').add(k,'key'));}catch{ k=await req(db.transaction('identity').objectStore('identity').get('key'));}return k;
    }finally{db.close();}
  }
  async function bind(uid){
    const {nonce}=await CtrlLive.api('/device/challenge','POST',{});const statement='ctrl-device-v1|'+uid+'|'+nonce;
    let proof;
    if(CtrlLive.native)proof=await CtrlLive.bridge({action:'install-proof',statement});
    else if(window.CtrlMobile)proof=await CtrlMobile.request({action:'install-proof',statement});
    else{
      const k=await webKey();const base=b=>btoa(String.fromCharCode(...new Uint8Array(b)));
      proof={publicKey:base(await crypto.subtle.exportKey('spki',k.publicKey)),signature:base(await crypto.subtle.sign({name:'ECDSA',hash:'SHA-256'},k.privateKey,new TextEncoder().encode(statement)))};
    }
    await CtrlLive.api('/device/bind','POST',{nonce,publicKey:proof.publicKey,signature:proof.signature});
  }
  return {bind};
})();

