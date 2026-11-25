if(/CtrlMobile(Android|iOS)\/1/.test(navigator.userAgent)){
  window.CtrlMobile=(()=>{
    let port;const tasks=new Map();let ready;
    const initialized=new Promise(r=>{ready=r;});
    function receive(raw){try{const v=JSON.parse(raw);const p=tasks.get(v.id);if(!p)return;tasks.delete(v.id);v.error?p.reject(Error(v.error)):p.resolve(v);}catch{}}
    if(window.webkit?.messageHandlers?.ctrl){port={postMessage:raw=>window.webkit.messageHandlers.ctrl.postMessage(raw)};ready();}
    window.addEventListener('message',e=>{if(e.data!=='ctrl.mobile.v1'||!e.ports[0]||port)return;port=e.ports[0];port.onmessage=m=>receive(m.data);port.start();ready();});
    async function request(args){
      await Promise.race([initialized,new Promise((_,reject)=>setTimeout(()=>reject(Error('Перезапустите приложение Ctrl.')),10000))]);
      const id=crypto.randomUUID();return new Promise((resolve,reject)=>{const timer=setTimeout(()=>{tasks.delete(id);reject(Error('Устройство не ответило.'));},args.action==='file-open'?180000:30000);
        tasks.set(id,{resolve:v=>{clearTimeout(timer);resolve(v);},reject:e=>{clearTimeout(timer);reject(e);}});port.postMessage(JSON.stringify({id,...args}));});
    }
    return {request,receive};
  })();
}

