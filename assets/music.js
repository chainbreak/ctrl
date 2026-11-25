const CtrlMusic=(()=>{
  const audio=new Audio();let uid='',db,selected='',url='',library=[];
  function open(){return db ||= new Promise((resolve,reject)=>{
    const op=indexedDB.open('ctrl-music',1);
    op.onupgradeneeded=()=>op.result.createObjectStore('tracks',{keyPath:'id'}).createIndex('owner','owner');
    op.onsuccess=()=>resolve(op.result);op.onerror=()=>reject(op.error);
  });}
  function close(){audio.pause();audio.removeAttribute('src');audio.load();if(url)URL.revokeObjectURL(url);url='';selected='';uid='';library=[];S.play=false;S.pos=0;}
  function owner(id){if(id!==uid){close();uid=id;}}
  async function refresh(){
    const who=uid,store=(await open()).transaction('tracks').objectStore('tracks');
    const rows=await new Promise((res,rej)=>{const op=store.index('owner').getAll(who);op.onsuccess=()=>res(op.result);op.onerror=()=>rej(op.error);});
    if(who!==uid)return;const byId=new Map(rows.map(r=>[r.id,r]));
    try{for(const t of await CtrlLive.api('/music/tracks'))if(!byId.has(t.id))byId.set(t.id,{id:t.id,name:t.title,seconds:t.seconds,created:t.created*1000});}catch(e){if(!rows.length)throw e;}
    if(who!==uid)return;library=[...byId.values()].sort((a,b)=>a.created-b.created);
    tracks.splice(0,tracks.length,...library.map(r=>({t:r.name,a:'',d:r.seconds})));
    S.tr=Math.max(0,library.findIndex(r=>r.id===selected));if(S.tab==='music')render();
  }
  async function add(file,who) {
    if(!file)return;
    if(!audio.canPlayType(file.type))throw Error('Этот аудиоформат не поддерживается.');
    const sample=new Audio(),temp=URL.createObjectURL(file);
    let seconds;
    try {seconds=await new Promise((res,rej)=>{const timeout=setTimeout(()=>rej(Error('Не удалось прочитать аудиофайл.')),10000);
      sample.onloadedmetadata=()=>{clearTimeout(timeout);res(Math.floor(sample.duration));};sample.onerror=()=>{clearTimeout(timeout);rej(Error('Не удалось прочитать аудиофайл.'));};sample.src=temp;});
    } finally{sample.removeAttribute('src');sample.load();URL.revokeObjectURL(temp);}
    if(!Number.isFinite(seconds)||seconds<1)throw Error('Не удалось определить длительность трека.');
    if(who!==uid)return;
    const name=file.name.replace(/\.[^.]+$/,'').slice(0,240)||'Трек';const {id}=await CtrlLive.api('/music/tracks','POST',{id:crypto.randomUUID(),title:name,seconds,size:file.size,mime:file.type});
    const chunk=1048576;for(let i=0;i*chunk<file.size;i++){
      const buf=new Uint8Array(await file.slice(i*chunk,(i+1)*chunk).arrayBuffer());let raw='';
      for(let j=0;j<buf.length;j+=16384)raw+=String.fromCharCode(...buf.subarray(j,j+16384));
      await CtrlLive.api('/music/'+id+'/parts/'+i,'POST',{data:btoa(raw)});
    }
    await CtrlLive.api('/music/'+id+'/finish','POST',{});if(who!==uid)return;
    const tx=(await open()).transaction('tracks','readwrite');tx.objectStore('tracks').put({id,owner:who,name,blob:file,created:Date.now(),seconds});
    await new Promise((res,rej)=>{tx.oncomplete=res;tx.onabort=()=>rej(tx.error);});await refresh();
  }
  async function action(a,id) {
    if(!uid)return;
    if(a==='upload'){
      const input=document.createElement('input'),who=uid;input.type='file';input.accept='audio/*';
      input.onchange=()=>add(input.files[0],who).catch(e=>toast(e.message));input.click();return;
    }
    if(!library.length)return;
    if(a==='play' && !audio.paused){audio.pause();S.play=false;render();return;}
    const index=a==='track'?Number(id):a==='next'?(S.tr+1)%library.length:a==='prev'?(S.tr+library.length-1)%library.length:S.tr;
    const track=library[index];if(!track)return;
    if(track.id!==selected){audio.pause();if(url)URL.revokeObjectURL(url);if(track.blob){url=URL.createObjectURL(track.blob);audio.src=url;}else{url='';audio.src=CtrlLive.mediaUrl((await CtrlLive.api('/music/'+track.id+'/listen','POST',{})).path);}selected=track.id;S.pos=0;}
    await audio.play();S.tr=index;S.play=true;render();
  }
  audio.ontimeupdate=()=>{S.pos=Math.floor(audio.currentTime);const p=document.getElementById('ppos'),bar=document.getElementById('pfill');if(p)p.textContent=mmss(S.pos);if(bar&&audio.duration)bar.style.width=(audio.currentTime/audio.duration*100)+'%';};
  audio.onended=()=>action('next').catch(e=>toast(e.message));return {owner,close,refresh,action};
})();

