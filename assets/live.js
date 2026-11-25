const CtrlLive = (() => {
  const enabled = !!document.querySelector('meta[name="ctrl-runtime"]') || (location.host==='ctrl.local' && !!window.chrome?.webview);
  const native = enabled && !!window.chrome?.webview;
  const histories = new Map();
  const pending = new Map(), drafts = new Map(), retry = new Map();
  let token='', user=null, generation=0, server='', lastList='', pendingSync=Promise.resolve(), sending=false, timer, sdk, sdkKey, offWallet, attempt, shop, mailId, unreadSnapshot=null;
  if(native)window.chrome.webview.addEventListener('message',({data})=>{
    const task=pending.get(data.id); if(!task)return;
    pending.delete(data.id); clearTimeout(task.timer);
    if(data.error)task.reject(Error(data.error)); else task.resolve(data);
  });
  function bridge(args) {
    return new Promise((resolve,reject)=>{
      const id=crypto.randomUUID();
      const timer=setTimeout(()=>{pending.delete(id); reject(Error('Сервер не отвечает.'));},48000);
      pending.set(id,{resolve,reject,timer}); window.chrome.webview.postMessage({id,...args});
    });
  }
  function persist(){return native?bridge({action:'session',token}):sessionStorage.setItem('ctrl-session',token);}
  async function api(path,method='GET',body) {
    const previous=token; let r;
    let human;if(method==='POST'&&['/register','/login','/ton/challenge','/support'].includes(path))human=await CtrlBot.check((await api('/auth-options')).turnstile);
    if(native)r=await bridge({action:'api',path:'/api'+path,method,body,token,human});
    else {
      const response=await fetch('/api'+path,{method,headers:{'Content-Type':'application/json',...(human?{'X-Ctrl-Turnstile':human}:{}),...(token?{Authorization:'Bearer '+token}:{})},
        body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(42000)});
      r={status:response.status,data:await response.json().catch(()=>null)};
    }
    if(r.status<200 || r.status>=300){
      if(r.status===401 && user && token===previous)reset();
      throw Object.assign(Error(r.data?.error || (r.status===401?'Войдите снова.':'Не удалось выполнить запрос.')),{status:r.status});
    }
    return r.data;
  }
  function clearWallet() { attempt=null; offWallet?.();offWallet=null;sdk?.closeModal();sdk?.setConnectRequestParameters(null); }
  function reset(){
    generation++;clearWallet();clearTimeout(timer);user=null;S.displayName='';token='';unreadSnapshot=null;persist();lastList='';histories.clear();drafts.clear();retry.clear();
    CtrlProfile.clear();CtrlWallet.clear();CtrlCommunity.clear();CtrlCalls.activate(null);CtrlPrivate.close();CtrlMusic.close();S.authenticated=false;S.previewMode=false;S.authMode='login';S.modal='';S.tab='chats';S.cur='';S.open=false;S.inboxOffline=false;S.draft='';
    S.email='';S.handle='';S.bio='';S.clicks=null;S.giftsLoaded=false;S.giftsOwned={};shop=null;
    chats.length=0;posts.length=0;items.length=0;tracks.length=0;render();
  }
  function useProfile(value){ user=value;S.displayName=value.name;S.verified=!!value.verified;CtrlMusic.owner(value.id); applyProfile({email:value.email||'',handle:value.username,bio:value.bio,clicks:value.clicks,premium:value.premium}); }
  async function enter(result){clearWallet();generation++;token=result.token;await persist();await CtrlInstall.bind(result.user.id);await CtrlPrivate.start(result.user.id);useProfile(result.user);await CtrlProfile.load();CtrlCalls.activate(result.user.id);S.modal='';S.tab='chats';S.cur='';S.open=false;chats.length=0;lastList='';S.showBal=false;for(const setting of await api('/me/settings')){try{const v=JSON.parse(setting.value);if(setting.key==='bal')S.showBal=v===true;else if(setting.key in S.opt)S.opt[setting.key]=v;else if(setting.key==='seenMode'||setting.key==='fontSize')S[setting.key]=v;}catch{}}render();await sync();watchInbox(generation);}
  async function start(){
    S.authMode='login';render();
    try {
      if(native){const init=await bridge({action:'init'});token=init.token;server=init.url;}
      else token=sessionStorage.getItem('ctrl-session')||'';
      if(token)await enter({token,user:await api('/me')});
    } catch(e){if(e.status===401)reset();toast(e.message);}
  }
  function connectionState(offline) {
    const badge=document.querySelector('#inbox-state');
    S.inboxOffline=offline;
    if(badge)badge.hidden=!offline;
  }
  async function watchInbox(gen) {
    let cursor='', legacy=false, delay=1000;
    while(user && generation===gen) {
      try {
        if(legacy)await new Promise(resolve=>setTimeout(resolve,2500));
        if(generation!==gen)return;
        const update=legacy?null:await api('/inbox/changes'+(cursor?'?since='+encodeURIComponent(cursor):''));
        if(generation!==gen)return;
        await sync();
        if(generation!==gen)return;
        if(update)cursor=update.cursor;
        connectionState(false);delay=1000;
      } catch(e) {
        if(generation!==gen || e.status===401)return;
        if(e.status===404 && !legacy){legacy=true;continue;}
        connectionState(true);
        await new Promise(resolve=>setTimeout(resolve,delay));
        delay=Math.min(delay*2,12000);
      }
    }
  }
  const formatTime=value=>value?new Date(value).toLocaleTimeString('ru',{hour:'2-digit',minute:'2-digit'}):'';
  function sync(force=false) {
    const gen=generation;
    pendingSync=pendingSync.catch(()=>{}).then(()=>{if(user && gen===generation)return refresh(gen,force);});
    return pendingSync;
  }
  async function refresh(gen,force) {
    const id=S.cur;
    const rows=await api('/conversations?compact=true');
    if(gen!==generation)return;
    const snapshot=new Map(rows.map(c=>[c.id,c.unread]));
    if(unreadSnapshot&&S.opt.ntf&&rows.some(c=>c.unread>(unreadSnapshot.get(c.id)||0)))tick();
    unreadSnapshot=snapshot;
    if(S.tab!=='chats'&&!force)return;
    const chat=rows.find(c=>c.id===id);
    const signature=JSON.stringify([id,rows]);
    let history=histories.get(id);
    if(chat && (!history || chat.version==null || history.version!==chat.version)) {
      const raw=await api('/conversations/'+id+'/messages');
      const messages=[];
      for(const m of raw){
        if(gen!==generation)return;
        messages.push({id:m.seq,mine:m.sender===user.id,text:await CtrlPrivate.opened(id,m),
          created:m.created,t:formatTime(m.created),edited:!!m.edited,re:!!m.reacted});
      }
      if(gen!==generation)return;
      history={version:chat.version,messages};
      histories.set(id,history);
    }
    if(gen!==generation || S.cur!==id)return;
    const rendered=(history?.messages||[]).map(m=>({...m,st:m.id<=chat?.peerRead?'read':'sent'}));
    if(chat && S.open && chat.unread && !document.hidden && rendered.length)
      await api('/conversations/'+id+'/read','POST',{seq:rendered.at(-1).id});
    if(gen!==generation || S.cur!==id || S.tab!=='chats')return;
    if(signature===lastList&&!force&&chat?.version!=null)return;
    lastList=signature;
    const merged=rows.map(c=>{
      const cached=histories.get(c.id);
      const recent=cached?.messages.at(-1);
      const preview=recent?.id===c.lastSeq && cached?.version===c.version ? recent?.text :
        c.lastSeq ? (c.unread?'Новое сообщение':'Сообщение') :
        c.lastText?.startsWith('SIG1:')?'Сообщение':c.lastText;
      return {id:c.id,name:c.name,ini:c.name.startsWith('+')?c.name.slice(-2):c.name.trim().split(/\s+/).slice(0,2).map(part=>Array.from(part)[0]).join('')||'?',
        kind:'dm',sub:c.username.startsWith('n.')?'NFT-номер':'@'+c.username,unread:c.unread,blocked:!!c.blocked,peerId:c.peerId,fee:c.fee,frame:c.frame,
        preview:preview?{text:preview,t:formatTime(c.lastAt)}:null, loaded:c.id===id && !!history, msgs:c.id===id?rendered:[]};
    });
    chats.splice(0,chats.length,...merged);
    render();
  }
  document.addEventListener('visibilitychange',()=>{
    if(!document.hidden&&user)sync(true).catch(()=>{});
  });
  async function send(){
    const c=cur(),text=S.draft.trim();if(!c||!text||c.blocked||sending)return;
    const gen=generation, edit=S.edit; let out; sending=true;
    
    try{
      out=await CtrlPrivate.seal(c.id,c.peerId,text,edit);
      await api('/conversations/'+c.id+'/messages'+(edit?'/'+edit:''),edit?'PATCH':'POST',{clientId:out.clientId,body:out.body,acceptedFee:c.fee||0});
      await CtrlPrivate.sent(c.id,out.clientId);
      if(gen!==generation)return; retry.delete(c.id);
      if(S.cur===c.id && S.draft.trim()===text){S.draft='';S.edit=0;const box=document.querySelector('#inp');if(box)box.value='';}
      drafts.delete(c.id);await sync(true);
    }catch(e){if(e.status===409&&out){await CtrlPrivate.sent(c.id,out.clientId);await sync(true);}toast(e.message);}finally{sending=false;}
  }
  function field(name,title,type='text',extra=''){return '<label>'+title+'<input class="in" name="'+name+'" type="'+type+'" '+extra+' required></label>';}
  function authHtml(){
    const mode=S.authMode, register=mode==='register', nft=mode==='nft', mail=mode==='mail';
    const title=mail?'Проверьте почту':nft?'Вход по номеру':register?'Создать аккаунт':'Вход в Ctrl';
    const fields=mail?field('code','Код из письма','text','inputmode="numeric" autocomplete="one-time-code" maxlength="8"'):
      nft?field('number','NFT-номер','tel','pattern="[+](333|999)[0-9]{8}" maxlength="12" placeholder="+33300001234"'):
      (register?field('name','Имя','text','maxlength="60" autocomplete="name"')+
        field('username','Имя пользователя','text','pattern="[a-z][a-z0-9_]{4,31}" placeholder="username" autocapitalize="none"'):'')+
      field('email',register?'Gmail':'Почта или имя пользователя',register?'email':'text','autocomplete="username" autocapitalize="none"')+
      field('password','Пароль','password',register?'minlength="12" placeholder="От 12 символов" autocomplete="new-password"':'autocomplete="current-password"');
    const primary=mail?'Подтвердить':nft?'Подключить кошелёк':register?'Зарегистрироваться':'Войти';
    const switchTo=(target,text,css='auth-link')=>key('authMode',text,css,'data-id="'+target+'"');
    const alternate=mail?switchTo('register','Назад'):register||nft?switchTo('login','Вернуться ко входу'):
      switchTo('nft',svg('wallet',18)+'Войти по NFT-номеру','wallet-entry');
    return '<div class="auth-stage"><aside class="auth-art" aria-hidden="true"><div class="auth-wordmark">Ctrl <span>Messenger</span></div></aside>'+
      '<section class="auth-flow"><div class="auth-card"><div class="mobile-brand">Ctrl Messenger</div><h1>'+title+'</h1>'+
      (nft?'<p class="auth-hint">Подтвердите владение номером в TON-кошельке.</p>':'')+
      '<form class="auth-form" data-form="'+(mail?'verify-mail':'desktop-auth')+'">'+fields+
      '<button class="key hi auth-submit">'+primary+svg('arrowRight',16)+'</button></form>'+alternate+
      (!mail&&!nft&&!register?'<div class="auth-register">Впервые в Ctrl? '+switchTo('register','Создать аккаунт')+'</div>':'')+
      '</div><footer class="auth-tools">'+key('helpDesk','Поддержка','auth-link')+(native?key('connection','Подключение','auth-link'):'')+'</footer></section></div>';
  }
  function dialog(title,html){return '<div class="mod" data-a="close"><section class="dlg" data-stop role="dialog" aria-modal="true" aria-label="'+title+'"><h2>'+title+'</h2>'+html+'</section></div>';}
  function connectionHtml(){return dialog('Подключение','<form class="auth-form" data-form="connection">'+field('url','Адрес сервера','url','value="'+esc(server)+'"')+'<button class="key hi">Подключить</button></form>'+key('close','Назад'));}
  async function wallet(){
    const config=await api('/ton/config'),k=config.manifestUrl+'|'+config.network;
    if(!window.TON_CONNECT_UI)await new Promise((resolve,reject)=>{const script=document.createElement('script');script.src='tonconnect.js';script.onload=resolve;script.onerror=()=>reject(Error('Не удалось открыть TON Connect.'));document.head.append(script);});
    if(!sdk || k!==sdkKey){sdkKey=k;sdk=new TON_CONNECT_UI.TonConnectUI({manifestUrl:config.manifestUrl,language:'ru',restoreConnection:false,analytics:{mode:'off'},uiPreferences:{theme:'DARK',borderRadius:'s',colorsSet:{DARK:{accent:'#ddd',connectButton:{background:'#eee',foreground:'#111'},background:{primary:'#141414',secondary:'#242424',qr:'#fff'}}}}});sdk.setConnectionNetwork(config.network);}
    return sdk;
  }
  async function nftLogin(number){
    clearWallet();const gen=generation,ui=await wallet();if(ui.connected)await ui.disconnect();
    const {payload}=await api('/ton/challenge','POST',{number});if(gen!==generation||S.authMode!=='nft')return;
    attempt=payload;
    offWallet=ui.onStatusChange(async w=>{
      if(!w||attempt!==payload)return;
      const proof=w.connectItems?.tonProof?.proof;
      if(proof && proof.payload!==payload)return;
      attempt=null;
      try{if(!proof)throw Error('Подтвердите вход в кошельке.');
        const result=await api('/ton/login','POST',{number,network:w.account.chain,address:w.account.address,proof});
        if(gen===generation && S.authMode==='nft')await enter(result);
      }catch(e){toast(e.message);}
    },()=>toast('Кошелёк отменил подключение.'));
    ui.setConnectRequestParameters({state:'ready',value:{tonProof:payload}});await ui.openModal();
  }
  let searchTimer, searchSequence=0;
  function searchPeople(input) {
    const query=input.value.trim().replace(/^@/,'');
    const sequence=++searchSequence,gen=generation;
    clearTimeout(searchTimer);
    const list=document.querySelector('#person-results');
    if(!list)return;
    list.replaceChildren();
    if(query.length<2 || (query.startsWith('+')&&!/^\+(333|999)\d{8}$/.test(query)))return;
    searchTimer=setTimeout(async()=>{
      try {
        const matches=await api('/users?q='+encodeURIComponent(query));
        if(!input.isConnected||input.value.trim().replace(/^@/,'')!==query||sequence!==searchSequence||gen!==generation)return;
        if(!matches.length){list.textContent='Никого не нашли';return;}
        list.innerHTML=matches.map(person=>'<button type="button" class="person-result" data-a="startChat" data-id="'+esc(person.id)+'"><span class="av">'+esc(Array.from(person.name)[0]||'')+
          '</span><span><b>'+esc(person.name)+'</b><small>'+esc(person.username.startsWith('n.')?'NFT-номер':'@'+person.username)+'</small></span>'+svg('arrowRight',16)+'</button>').join('');
      } catch(e) {if(input.isConnected && sequence===searchSequence && gen===generation)list.textContent=e.message;}
    },200);
  }
  async function startChat(peerId) {
    const gen=generation;
    const chat=await api('/conversations','POST',{peerId});
    if(gen!==generation)return;
    drafts.set(S.cur,S.draft);S.cur=chat.id;S.open=true;S.tab='chats';S.filter='all';S.q='';
    S.draft=drafts.get(chat.id)||'';S.edit=0;S.menu=false;S.modal='';
    await sync(true);
    document.getElementById('inp')?.focus();
  }
  document.addEventListener('input',event=>{if(event.target.matches('[data-form="newchat"] [name="handle"]'))searchPeople(event.target);});
  async function form(el){
    const name=el.dataset.form;
    if(await CtrlWallet.form(el))return true;
    if(await CtrlShop.form(el))return true;
    if(await CtrlCommunity.form(el))return true;
    if(!['desktop-auth','connection','newchat','ton-order','post','verify-mail','account-password'].includes(name))return false;
    const v=Object.fromEntries(new FormData(el));const submit=el.querySelector('button[type="submit"],button:not([type])');
    if(submit?.disabled)return true;if(submit)submit.disabled=true;
    try{
      if(name==='desktop-auth'){
        if(S.authMode==='nft')await nftLogin(v.number);
        else {const result=await api(S.authMode==='register'?'/register':'/login','POST',S.authMode==='register'?v:{loginName:v.email,password:v.password});if(result.challengeId){mailId=result.challengeId;S.authMode='mail';render();}else await enter(result);}
      }else if(name==='verify-mail'){await enter(await api('/verify-email','POST',{id:mailId,code:v.code}));}
      else if(name==='account-password'){await api('/me/password','POST',{old:v.old,next:v.next});S.modal='';render();toast('Пароль изменён.');}
      else if(name==='connection'){const r=await bridge({action:'connect',url:v.url});server=r.url;reset();}
      else if(name==='newchat'){const q=v.handle.trim().replace(/^@/,'');if(q.toLowerCase()==='ctrlsup'){S.modal='';CtrlCommunity.action('helpDesk',{});return true;}if(q.toLowerCase()==='ctrl'){S.modal='';CtrlCommunity.action('news',{});return true;}const results=await api('/users?q='+encodeURIComponent(q));
        const peer=q.startsWith('+')?results[0]:results.find(x=>x.username===q.toLowerCase()||x.alias===q.toLowerCase());if(!peer)throw Error('Пользователь не найден.');
        await startChat(peer.id);
      }else if(name==='post'){const r=await api('/feed','POST',{text:v.text});if(r.pending)toast('Публикация передана на проверку.');S.modal='';await loadTabData('feed');}
      else await order(v);
    }catch(e){toast(e.message);}finally{if(submit)submit.disabled=false;}return true;
  }
  async function request(path,method,body){
    if(path==='/me'&&method==='PATCH'){const u=await api('/me',method,{name:user.name,username:body.handle?.replace(/^@/,''),bio:body.bio});useProfile(u);return {user:{email:u.email||'',handle:u.username,bio:u.bio,clicks:u.clicks,premium:u.premium}};}
    return api(path,method,body);
  }
  function action(a,d){
    if(a==='startChat'){runApi(()=>startChat(d.id));return true;}
    if(a==='bal'){runApi(async()=>{const value=!S.showBal;await api('/me/settings','PATCH',{key:'bal',value});S.showBal=value;render();});return true;}
    if(a==='cloudPassword'){S.modal='account-password';render();return true;}
    if(a==='support'){CtrlCommunity.action('helpDesk',{});return true;}
    if(a==='attachFile'){CtrlFiles.select();return true;}
    if(a==='downloadFile'){runApi(()=>CtrlFiles.download(d.id));return true;}
    if(CtrlProfile.action(a,d))return true;
    if(CtrlWallet.action(a,d))return true;
    if(CtrlShop.action(a,d))return true;
    if(CtrlCommunity.action(a,d))return true;
    if(a==='deviceBlock'){runApi(async()=>{await api('/conversations/'+S.cur+'/device-block','POST',{});S.menu=false;await sync(true);});return true;}
    if(a==='audioCall'||a==='videoCall'){runApi(()=>CtrlCalls.start(a==='videoCall'?'video':'audio'));return true;}
    if(['upload','play','track','prev','next'].includes(a)){runApi(()=>CtrlMusic.action(a,d.id));return true;}
    if(a==='like'){runApi(async()=>{await api('/feed/posts/'+d.id+'/likes','POST',{});await loadTabData('feed');});return true;}
    if(a==='authMode'){clearWallet();generation++;S.authMode=d.id;S.modal='';render();return true;}
    if(a==='connection'||a==='composePost'){S.modal=a==='connection'?'connection':'post';render();return true;}
    if(a==='logout'){runApi(async()=>{await api('/logout','POST',{});reset();});return true;}
    if(a==='chat'){drafts.set(S.cur,S.draft);S.cur=d.id;S.open=true;S.draft=drafts.get(d.id)||'';S.edit=0;S.menu=false;render();runApi(()=>sync(true));return true;}
    if(a==='del'||a==='react'){const c=cur();if(c)runApi(async()=>{await api('/conversations/'+c.id+'/messages/'+d.id+(a==='react'?'/reaction':''),a==='react'?'POST':'DELETE',{});await sync(true);});return true;}
    if(a==='tonShop'){S.modal='market-order';render();return true;}
    if(a==='dodel'){runApi(async()=>{await api('/me','DELETE');reset();});return true;}
    return false;
  }
  function modalContent(){
    const profile=CtrlProfile.modal();if(profile)return profile;
    const walletDialog=CtrlWallet.modal();if(walletDialog)return walletDialog;
    const shopDialog=CtrlShop.modal();if(shopDialog)return shopDialog;
    const community=CtrlCommunity.modal();if(community)return community;
    if(S.modal==='account-password')return dialog('Сменить пароль','<form class="auth-form" data-form="account-password">'+field('old','Текущий пароль','password','autocomplete="current-password"')+field('next','Новый пароль','password','minlength="12" autocomplete="new-password"')+'<button class="key hi">Сохранить</button></form>'+key('close','Закрыть'));
    if(S.modal==='newchat')return dialog('Новый чат','<form data-form="newchat" class="auth-form"><label>Имя пользователя или NFT-номер<input autofocus class="in" name="handle" placeholder="@username или +333…" autocomplete="off" required></label><div id="person-results" role="status"></div><div class="ac">'+key('close','Отмена','','type="button"')+'<button type="submit" class="key hi">Открыть чат</button></div></form>');
    if(S.modal==='connection')return connectionHtml();
    if(S.modal==='post')return dialog('Новая публикация','<form data-form="post" class="auth-form"><textarea class="in" name="text" maxlength="4000" required></textarea><button class="key hi">Опубликовать</button></form>'+key('close','Отмена'));
    if(S.modal==='market-order')return dialog('Маркет TON','<form data-form="ton-order" class="auth-form"><label>Актив<select name="kind"><option value="number">NFT-номер</option><option value="gift">Подарок</option><option value="username">Юзернейм</option></select></label><label>Номер<input class="in" name="number" type="tel" placeholder="+33300001234"></label><label data-auction hidden>Юзернейм<input class="in" name="handle" placeholder="@username"></label><label data-auction hidden>Ставка в TAP<input class="in" name="amount" inputmode="decimal"></label><label>Операция<select name="operation"><option value="quote">Проверить цену и баланс</option><option value="deposit">Внести TAP</option><option value="buy">Купить</option><option data-auction hidden value="bid">Сделать ставку</option><option data-auction hidden value="settle">Завершить торги</option><option value="withdraw">Вывести остаток TAP</option></select></label><p id="tonQuote" role="status">'+esc(shop||'')+'</p><button class="key hi">Продолжить</button></form>'+key('close','Закрыть'));
    return modalHtml();
  }
  async function order(v){
    const ui=await wallet();
    if(!ui.connected){await ui.openModal();throw Error('Подключите кошелёк и повторите операцию.');}
    const response=await api('/ton/order','POST',{...v,wallet:ui.account.address,network:ui.account.chain});
    shop=response.summary;
    const output=document.getElementById('tonQuote');if(output)output.textContent=shop;
    if(response.transaction){await ui.sendTransaction(response.transaction);toast('Запрос подписан. Проверьте баланс после подтверждения в TON.');}
  }
  return {enabled,native,bridge,start,api,wallet,mediaUrl:path=>new URL(path,native?server:location.origin).href,request,send,sync,form,action,authHtml,connectionHtml,modalContent};
})();


document.addEventListener('change',e=>{if(e.target.matches('[data-form=ton-order] [name=kind]')){const f=e.target.form;const a=e.target.value==='username';f.querySelectorAll('[data-auction]').forEach(x=>x.hidden=!a);f.elements.number.closest('label').hidden=e.target.value!=='number';f.elements.operation.querySelector('[value=buy]').hidden=a;f.elements.operation.value='quote';}});
