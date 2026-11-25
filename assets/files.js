const CtrlFiles=(()=>{
  const prefix='CTRL-FILE/1 ',chunk=1048576,active=new Set();
  function encode(bytes){let s='';for(let i=0;i<bytes.length;i+=16384)s+=String.fromCharCode(...bytes.subarray(i,i+16384));return btoa(s);}
  function decode(text){return Uint8Array.from(atob(text),c=>c.charCodeAt(0));}
  function iv(index){const b=new Uint8Array(12);new DataView(b.buffer).setUint32(8,index);return b;}
  function parse(text){
    if(!text?.startsWith(prefix))return null;
    try{const m=JSON.parse(text.slice(prefix.length));if(!/^[0-9a-f]{32}$/.test(m.id)||typeof m.name!=='string'||m.name.length>240||!Number.isSafeInteger(m.size)||m.size<1||m.size>2000000000||decode(m.key).length!==32)return null;return m;}catch{return null;}
  }
  function view(text,seq){const m=parse(text);return m?'<button class="key file-item" data-a="downloadFile" data-id="'+esc(seq)+'">'+esc(m.name)+' <small>'+Math.max(1,Math.ceil(m.size/1024)).toLocaleString('ru')+' КБ</small></button>':esc(text);}
  function select(){const input=document.createElement('input');input.type='file';input.onchange=()=>{if(input.files[0])runApi(()=>upload(input.files[0]));};input.click();}
  async function upload(file){
    const c=cur();if(!c||active.has(c.id))return;
    const fee=c.fee||0;
    if(fee&&!confirm('Отправить файл за '+fee+' Кликов?'))return;
    active.add(c.id);let id,completed=false;
    try{
      const result=await CtrlLive.api('/files','POST',{chat:c.id,size:file.size});id=result.id;
      const key=await crypto.subtle.generateKey({name:'AES-GCM',length:256},true,['encrypt']);
      const raw=new Uint8Array(await crypto.subtle.exportKey('raw',key));
      for(let i=0;i*chunk<file.size;i++){
        const plain=await file.slice(i*chunk,(i+1)*chunk).arrayBuffer();
        const data=encode(new Uint8Array(await crypto.subtle.encrypt({name:'AES-GCM',iv:iv(i)},key,plain)));
        await CtrlLive.api('/files/'+id+'/parts/'+i,'POST',{data});
      }
      await CtrlLive.api('/files/'+id+'/complete','POST',{});completed=true;
      const descriptor=prefix+JSON.stringify({id,name:file.name.slice(0,240),size:file.size,key:encode(raw)});
      const out=await CtrlPrivate.seal(c.id,c.peerId,descriptor,0);
      await CtrlLive.api('/conversations/'+c.id+'/messages','POST',{clientId:out.clientId,body:out.body,acceptedFee:fee,fileId:id});
      await CtrlPrivate.sent(c.id,out.clientId);await CtrlLive.sync(true);
    }finally{active.delete(c.id);if(id&&!completed)await CtrlLive.api('/files/'+id,'DELETE').catch(()=>{});}
  }
  async function download(seq){
    const m=parse(cur()?.msgs.find(x=>String(x.id)===String(seq))?.text);if(!m||active.has(m.id))return;
    active.add(m.id);let writer;
    try{
      const filename=m.name.replace(/[<>:"/\\|?*\u0000-\u001f]/g,'_')||'file';
      if(window.CtrlMobile){const d=await CtrlMobile.request({action:'file-open',name:filename});if(d.cancelled)return;
        writer={write:bytes=>CtrlMobile.request({action:'file-write',handle:d.handle,data:encode(new Uint8Array(bytes))}),close:()=>CtrlMobile.request({action:'file-finish',handle:d.handle}),abort:()=>CtrlMobile.request({action:'file-cancel',handle:d.handle})};
      }else if(window.showSaveFilePicker){let handle;try{handle=await showSaveFilePicker({suggestedName:filename});}catch(e){if(e.name==='AbortError')return;throw e;}writer=await handle.createWritable();}
      if(!writer&&m.size>134217728)throw Error('Для сохранения этого файла откройте чат в настольном Ctrl.');
      const buffers=[],key=await crypto.subtle.importKey('raw',decode(m.key),'AES-GCM',false,['decrypt']);
      for(let i=0;i*chunk<m.size;i++){
        const part=await CtrlLive.api('/files/'+m.id+'/parts/'+i);
        const bytes=await crypto.subtle.decrypt({name:'AES-GCM',iv:iv(i)},key,decode(part.data));
        if(bytes.byteLength!==Math.min(chunk,m.size-i*chunk))throw Error('Файл повреждён.');
        if(writer)await writer.write(bytes);else buffers.push(bytes);
      }
      if(writer){await writer.close();writer=null;}else{const url=URL.createObjectURL(new Blob(buffers));const a=document.createElement('a');a.href=url;a.download=filename;a.click();setTimeout(()=>URL.revokeObjectURL(url),60000);}
    }finally{active.delete(m.id);if(writer)await writer.abort().catch(()=>{});}
  }
  return {view,select,download,upload,summary:text=>parse(text)?.name||text,isFile:text=>!!parse(text)};
})();

