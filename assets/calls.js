const CtrlCalls=(()=>{
  let uid='',row=null,pc=null,local=null,remote=null,own=false,inFlight=false,pollTimer,stage='',lastPulse=0;
  const by=s=>document.querySelector(s);
  async function end(notify=true){
    clearTimeout(pollTimer);const id=row?.id;row=null;own=false;stage='';
    if(pc){pc.onconnectionstatechange=null;pc.close();pc=null;}
    for(const t of local?.getTracks()||[])t.stop();local=null;remote=null;
    document.getElementById('ctrl-call')?.remove();
    if(notify&&id)await CtrlLive.api('/calls/'+id,'DELETE').catch(()=>{});
    if(uid)pollTimer=setTimeout(poll,2200);
  }
  function panel(){
    let pane=by('#ctrl-call');if(!pane){pane=document.createElement('aside');pane.id='ctrl-call';pane.className='card call-panel';pane.setAttribute('aria-live','polite');document.body.append(pane);}
    pane.replaceChildren();const title=document.createElement('b');const c=chats.find(x=>x.id===row?.chat);
    title.textContent=(row?.mode==='video'?'Видеозвонок':'Аудиозвонок')+(c?' · '+c.name:'');pane.append(title);
    const status=document.createElement('span');status.className='mu';status.textContent=stage;pane.append(status);
    if(remote){const v=document.createElement('video');v.autoplay=true;v.playsInline=true;v.srcObject=remote;pane.append(v);v.play().catch(()=>{});}
    const buttons=document.createElement('div');buttons.className='ac';pane.append(buttons);
    function button(label,act){const b=document.createElement('button');b.className='key';b.textContent=label;b.onclick=()=>act().catch(e=>toast(e.message));buttons.append(b);}
    if(!own && !pc && row?.state==='ringing')button('Ответить',accept);
    if(local)button(local.getAudioTracks()[0]?.enabled?'Выключить микрофон':'Включить микрофон',async()=>{for(const t of local.getAudioTracks())t.enabled=!t.enabled;panel();});
    button('Завершить',()=>end());
  }
  async function prepare(mode){
    const iceServers=await CtrlLive.api('/calls/ice');
    local=await navigator.mediaDevices.getUserMedia({audio:true,video:mode==='video'});
    pc=new RTCPeerConnection({iceServers});
    for(const track of local.getTracks())pc.addTrack(track,local);
    pc.ontrack=e=>{remote=e.streams[0]||new MediaStream([e.track]);panel();};
    pc.onconnectionstatechange=()=>{if(!pc)return;if(pc.connectionState==='failed'){toast('Не удалось соединить звонок.');end();}else if(pc.connectionState==='connected'){stage='На связи';panel();}};
  }
  async function gathered(type){
    const conn=pc;
    await conn.setLocalDescription(type==='offer'?await conn.createOffer():await conn.createAnswer());
    if(conn.iceGatheringState!=='complete')await new Promise(resolve=>{
      let t;const finish=()=>{clearTimeout(t);conn.removeEventListener('icegatheringstatechange',changed);resolve();};
      const changed=()=>{if(conn.iceGatheringState==='complete')finish();};t=setTimeout(finish,9000);conn.addEventListener('icegatheringstatechange',changed);
    });
    return JSON.stringify(conn.localDescription);
  }
  async function start(mode){
    const c=cur();if(!c||!c.peerId||inFlight||row)return;inFlight=true;
    try{await prepare(mode);const sdp=await gathered('offer');
      const out=await CtrlPrivate.seal(c.id,c.peerId,sdp,'rtc');
      const r=await CtrlLive.api('/calls','POST',{chat:c.id,mode,offer:out.body});await CtrlPrivate.sent(c.id,out.clientId);
      row={...r,chat:c.id,mode,state:'ringing'};own=true;stage='Вызов…';panel();
    }catch(e){await end();throw e;}finally{inFlight=false;}
  }
  async function accept(){
    if(!row||inFlight)return;inFlight=true;
    try{const call=row;await prepare(call.mode);
      const description=await CtrlPrivate.opened(call.chat,{sender:call.caller,body:call.offer});await pc.setRemoteDescription(JSON.parse(description));
      const out=await CtrlPrivate.seal(call.chat,call.caller,await gathered('answer'),'rtc');
      await CtrlLive.api('/calls/'+call.id+'/answer','POST',{answer:out.body});await CtrlPrivate.sent(call.chat,out.clientId);
      stage='Соединение…';panel();
    }catch(e){await end();throw e;}finally{inFlight=false;}
  }
  async function poll(){
    clearTimeout(pollTimer);const who=uid;if(!who)return;
    try{
      if(inFlight)return;
      const current=await CtrlLive.api('/calls');if(who!==uid||inFlight)return;
      if(!current.id){if(row)await end(false);return;}
      if(!row && current.callee===uid && current.state==='ringing'){row=current;own=false;stage='Входящий звонок';if(S.opt.ntf)tick();panel();}
      if(own && pc && current.id===row?.id && current.answer && !pc.remoteDescription){
        const description=await CtrlPrivate.opened(current.chat,{sender:current.callee,body:current.answer});
        await pc.setRemoteDescription(JSON.parse(description));stage='Соединение…';panel();
      }
      if(row && pc && current.state==='active' && Date.now()-lastPulse>20000){await CtrlLive.api('/calls/'+row.id+'/pulse','POST',{});lastPulse=Date.now();}
    }catch{}finally{clearTimeout(pollTimer);if(uid)pollTimer=setTimeout(poll,row?1300:3600);}
  }
  function activate(id){if(uid===id)return;uid=id;clearTimeout(pollTimer);if(uid)pollTimer=setTimeout(poll,500);else end(false);}
  return {start,activate};
})();

