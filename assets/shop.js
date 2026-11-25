const CtrlShop=(()=>{
  let catalog={},requests=[],rooms=[];
  const status={submitted:'Ожидает оплаты',paid:'На проверке',approved:'Подтверждено',rejected:'Отклонено'};
  const price=c=>new Intl.NumberFormat('ru',{style:'currency',currency:'USD'}).format(c/100);
  async function open(name){
    catalog=await CtrlLive.api('/shop');
    if(name==='noer'){requests=await CtrlLive.api('/noer');const me=await CtrlLive.api('/me');rooms=(await CtrlLive.api('/community')).rooms.filter(r=>r.owner===me.id);}
    S.modal='pay-'+name;render();
  }
  async function pay(kind,ref){
    const r=await CtrlLive.api('/shop/checkout','POST',{kind,reference:ref,operation:crypto.randomUUID()});
    const u=new URL(r.url);if(u.protocol!=='https:'||u.hostname!=='checkout.stripe.com')throw Error('Не удалось открыть оплату.');
    if(CtrlLive.native)await CtrlLive.bridge({action:'checkout',url:u.href});else window.location.assign(u.href);
  }
  function action(a,d){
    if(['buy','buyclk','prem','noer'].includes(a)){runApi(()=>open(['buy','buyclk'].includes(a)?'clicks':a==='prem'?'premium':'noer'));return true;}
    if(a==='checkout'){runApi(()=>pay(S.modal.slice(4),d.id||''));return true;}return false;
  }
  function modal(){if(!S.modal?.startsWith('pay-'))return '';
    const kind=S.modal.slice(4),title={clicks:'Клики',premium:'Ctrl+ Premium',noer:'Верификация NOER'}[kind];let body='';
    if(kind==='noer'){body=requests.map(r=>'<p>'+esc(status[r.state]||r.state)+(catalog.ready&&r.state==='submitted'?key('checkout','Оплатить $100','sm','data-id="'+esc(r.id)+'"'):'')+'</p>').join('');
      body+='<form data-form="noer" class="auth-form"><label>Заявка<select name="room"><option value="">Мой профиль</option>'+rooms.map(r=>'<option value="'+esc(r.id)+'">'+esc(r.title)+'</option>').join('')+'</select></label><label>Документ владельца<input name="document" type="file" accept="application/pdf,image/png,image/jpeg" required></label><p class="mu">Документ увидит сотрудник проверки. Стоимость заявки — $100.</p><button class="key hi">Подать заявку</button></form>';}
    else{const cents=kind==='clicks'?catalog.clickPrice:catalog.premiumPrice,label=kind==='clicks'?catalog.clickUnits+' Кликов':'Premium на 30 дней';
      body=catalog.ready&&cents>0?key('checkout',label+' · '+price(cents),'hi'):'<p class="mu">Оплата пока недоступна.</p>';}
    return '<div class="mod" data-a="close"><section class="dlg" data-stop role="dialog" aria-modal="true"><h2>'+title+'</h2>'+body+key('close','Закрыть','sm')+'</section></div>';
  }
  async function form(el){
    if(el.dataset.form!=='noer')return false;const btn=el.querySelector('button');if(btn.disabled)return true;btn.disabled=true;
    try{const f=el.elements.document.files[0];if(!f||f.size>2000000)throw Error('Размер документа — до 2 МБ.');
      const encoded=await new Promise((res,rej)=>{const reader=new FileReader();reader.onload=()=>res(reader.result.split(',')[1]);reader.onerror=()=>rej(Error('Не удалось прочитать файл.'));reader.readAsDataURL(f);});
      await CtrlLive.api('/noer','POST',{room:el.elements.room.value||null,document:encoded});await open('noer');
    }catch(e){toast(e.message);}finally{btn.disabled=false;}return true;
  }
  return {action,modal,form};
})();

