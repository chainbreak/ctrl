const CtrlWallet=(()=>{
  let balance=null,address='',editions=[],attempt=0,unsubscribe;
  const api=(...args)=>CtrlLive.api(...args);
  async function refresh(){try{const w=await api('/wallet');address=w.address||'';balance=w.balance??null;}catch(e){address='';balance=null;if(e.status!==503)throw e;}S.walletBalance=balance==null?null:Number(balance);if(S.tab==='wallet')render();}
  function html(){return '<div class="pg"><div class="profile-heading"><h1>Кошелёк</h1>'+key('walletRefresh','Обновить','sm')+'</div><section class="card"><span class="mu">TAP</span><h2>'+esc(balance??'—')+'</h2>'+(address?'<p class="wallet-address">'+esc(address)+'</p><div class="ac">'+key('copyWallet','Скопировать адрес','sm')+key('sendTap','Перевести','hi')+'</div>':key('connectWallet','Подключить кошелёк','hi'))+'</section>'+key('showEditions','Мои NFT-подарки','sm')+key('bindHandle','Привязать NFT-юзернейм','sm')+'</div>';}
  async function link(){
    const gen=++attempt,ui=await CtrlLive.wallet();if(ui.connected)await ui.disconnect();unsubscribe?.();
    const {payload}=await api('/wallet/challenge','POST',{});
    unsubscribe=ui.onStatusChange(async w=>{if(!w||gen!==attempt)return;const proof=w.connectItems?.tonProof?.proof;if(proof?.payload!==payload)return;
      unsubscribe?.();unsubscribe=null;try{await api('/wallet/link','POST',{proof,address:w.account.address,network:w.account.chain});await refresh();toast('Кошелёк подключён.');}catch(e){toast(e.message);}
    });ui.setConnectRequestParameters({state:'ready',value:{tonProof:payload}});await ui.openModal();
  }
  async function approve(tx){
    const ui=await CtrlLive.wallet();if(!ui.connected){ui.setConnectRequestParameters(null);await ui.openModal();throw Error('Подключите кошелёк и повторите операцию.');}
    if(ui.account.address!==tx.from||ui.account.chain!==tx.network)throw Error('Выберите привязанный кошелёк и сеть.');
    await ui.sendTransaction(tx);toast('Операция отправлена в TON.');
  }
  async function edition(gift,operation){
    const r=await api('/gifts/edition','POST',{gift,operation});if(r.minted){toast('NFT выпущен.');return;}
    await approve(r.transaction);
  }
  async function handle(a,id){
    if(a==='connectWallet')return link();if(a==='walletRefresh')return refresh();
    if(a==='copyWallet'){await navigator.clipboard.writeText(address);toast('Адрес скопирован.');return;}
    if(a==='bindHandle'){S.modal='bind-handle';render();return;}
    if(a==='sendTap'){S.modal='send-tap';render();return;}
    if(a==='showEditions'){editions=await api('/gifts/editions');S.modal='editions';render();return;}
    if(a==='resumeEdition'){const row=editions.find(x=>x.operation===id);if(row)await edition(row.gift,row.operation);return;}
    if(a==='upgradeGift'){if(!confirm('Обменять подарок и 25 Кликов на право выпуска NFT? Комиссию сети покажет кошелёк.'))return;
      const op=crypto.randomUUID();try{await edition(id,op);}finally{await loadTabData('gifts');}return;}
  }
  function action(a,d){if(!['connectWallet','walletRefresh','copyWallet','bindHandle','sendTap','showEditions','resumeEdition','upgradeGift'].includes(a))return false;runApi(()=>handle(a,d.id));return true;}
  function modal(){
    let content,title;if(S.modal==='send-tap'){title='Перевести TAP';content='<form data-form="send-tap" class="auth-form"><label>TON-адрес<input class="in" name="address" required></label><label>Сумма TAP<input class="in" name="amount" inputmode="decimal" required></label><button class="key hi">Подтвердить в кошельке</button></form>';}
    else if(S.modal==='bind-handle'){title='NFT-юзернейм';content='<form class="auth-form" data-form="bind-handle"><label>Юзернейм<input class="in" name="handle" placeholder="@name" required></label><button class="key hi">Привязать</button></form>';}
    else if(S.modal==='editions'){title='Мои NFT-подарки';content=editions.map(x=>'<p>'+esc(giftDefinitions.find(g=>g.id===x.gift)?.name||x.gift)+' '+key('resumeEdition','Проверить выпуск','sm','data-id="'+esc(x.operation)+'"')+'</p>').join('')||'<p>Выпусков пока нет.</p>';}else return '';
    return '<div class="mod" data-a="close"><section data-stop class="dlg" role="dialog"><h2>'+title+'</h2>'+content+key('close','Закрыть','sm')+'</section></div>';
  }
  async function form(el){if(el.dataset.form==='bind-handle'){try{await api('/wallet/handle','POST',{handle:el.elements.handle.value});S.modal='';render();toast('Теперь вас можно найти по NFT-юзернейму.');}catch(e){toast(e.message);}return true;}if(el.dataset.form!=='send-tap')return false;const b=el.querySelector('button');if(b.disabled)return true;b.disabled=true;try{const tx=await api('/wallet/transfer','POST',Object.fromEntries(new FormData(el)));await approve(tx);S.modal='';render();}catch(e){toast(e.message);}finally{b.disabled=false;}return true;}
  function clear(){attempt++;unsubscribe?.();unsubscribe=null;balance=null;address='';editions=[];S.walletBalance=null;}
  return {html,action,form,modal,refresh,clear};
})();

