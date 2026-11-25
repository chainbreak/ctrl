const CtrlCommunity=(()=>{
  let data={},active=null,posts=[],options={},ticket,reply='',roomTimer;
  const button=(a,t,id)=>key(a,t,'sm',id?'data-id="'+esc(id)+'"':'');
  function dialog(title,inside){return '<div class="mod" data-a="close"><section class="dlg" role="dialog" aria-modal="true" data-stop><h2>'+esc(title)+'</h2>'+inside+button('close','Закрыть')+'</section></div>';}
  function formBox(kind,fields,submit){return '<form class="auth-form" data-form="'+kind+'">'+fields+'<button class="key hi">'+submit+'</button></form>';}
  const input=(label,name,more='')=>'<label>'+label+'<input class="in" name="'+name+'" required '+more+'></label>';
  const body='<label>Текст<textarea class="in" name="text" maxlength="2000" required></textarea></label>';
  const modes=new Set(['news','community','room','roomCreate','story','helpDesk','accountOptions','roomJoin']);
  async function load(){data=await CtrlLive.api('/community');}
  function modal(){
    if(!modes.has(S.modal))return '';
    if(S.modal==='news')return dialog('@ctrl',(data.notices||[]).map(n=>'<p>'+esc(n.body)+'</p>').join('')||'<p>Уведомлений пока нет.</p>');
    if(S.modal==='helpDesk')return dialog('Поддержка',formBox('helpDesk',input('Ваша почта','email','type="email" maxlength="200"')+body,'Отправить')+(ticket?'<p class="mu">Обращение '+esc(ticket.id.slice(0,8))+'</p>'+button('ticketStatus','Проверить ответ'):'')+(reply?'<p>'+esc(reply)+'</p>':''));
    if(S.modal==='accountOptions'){
      const frame=options.settings?.frame||'',owned=new Set((options.frames||[]).map(x=>x.frame));
      return dialog('Оформление и Клики',formBox('accountOptions',input('Цена входящего сообщения в Кликах','fee','type="number" min="0" max="10000" value="'+Number(options.settings?.fee||0)+'"')+'<label>Рамка аватара<select name="frame">'+[['','Без рамки'],['silver','Серебро'],['graphite','Графит']].map(([v,n])=>'<option value="'+v+'" '+(frame===v?'selected':'')+'>'+n+(v&&!owned.has(v)?' · 25 Кликов':'')+'</option>').join('')+'</select></label>','Сохранить'));
    }
    if(S.modal==='community')return dialog('Каналы и истории','<div class="ac">'+button('roomCreate','Создать')+button('roomJoin','По приглашению')+button('story','Добавить историю')+'</div><div class="community-list">'+(data.rooms||[]).map(r=>button('room',r.title+(r.verified?' · NOER':''),r.id)).join('')+'</div>'+(data.stories||[]).map(s=>'<article><b>@'+esc(s.username)+'</b><p>'+esc(s.body)+'</p></article>').join('')+(data.notices||[]).map(n=>'<article><b>@ctrl</b><p>'+esc(n.body)+'</p></article>').join(''));
    if(S.modal==='roomCreate')return dialog('Новое сообщество',formBox('roomCreate',input('Название','title','maxlength="70"')+'<label>Тип<select name="kind"><option value="channel">Канал</option><option value="group">Группа по приглашению</option></select></label>','Создать'));
    if(S.modal==='roomJoin')return dialog('Приглашение',formBox('roomJoin',input('Код приглашения','id','pattern="[a-f0-9]{32}"'),'Вступить'));
    if(S.modal==='story')return dialog('История на 24 часа',formBox('story',body,'Опубликовать'));
    if(!active)return '';
    return dialog(active.title,'<div class="ac">'+button('copyInvite','Скопировать приглашение')+button('community','Назад')+button('roomReport','Пожаловаться')+button('roomMute','Скрыть')+'</div>'+(!active.joined?button('joinRoom','Присоединиться'):(active.kind==='group'||active.owner===data.me?formBox('roomPost',body,'Опубликовать'):''))+'<div class="community-list">'+posts.map(p=>'<article><b>@'+esc(p.username)+'</b><p>'+esc(p.body)+'</p></article>').join('')+'</div>');
  }
  function pollRoom(id){
    clearTimeout(roomTimer);roomTimer=setTimeout(async()=>{
      if(S.modal!=='room'||active?.id!==id||!active.joined)return;
      try{const rows=await CtrlLive.api('/community/'+id+'/posts');if(S.modal!=='room'||active?.id!==id)return;
        if(active.kind==='group'){for(const p of rows)p.body=await CtrlPrivate.opened(id,{body:p.body,sender:p.sender});}
        if(JSON.stringify(rows)!==JSON.stringify(posts)){posts=rows;render();}
      }catch{}finally{if(S.modal==='room'&&active?.id===id)pollRoom(id);}
    },4500);
  }
  async function open(a,id){
    if(a==='helpDesk'){try{ticket=JSON.parse(sessionStorage.getItem('ctrl-help-ticket'));}catch{}reply='';}
    if(a==='ticketStatus'){const r=await CtrlLive.api('/support/status','POST',ticket);reply=r.answer||'Обращение ожидает ответа.';render();return;}
    if(a==='roomMute'){await CtrlLive.api('/community/'+active.id+'/mute','POST',{});return open('community');}
    if(a==='roomReport'){if(!confirm('Отправить жалобу и последние пять сообщений сотруднику поддержки?'))return;await CtrlLive.api('/community/'+active.id+'/report','POST',{messages:posts.slice(0,5).map(p=>({id:p.id,text:p.body}))});toast('Жалоба отправлена.');return;}
    if(a==='copyInvite'){await navigator.clipboard.writeText(active.id);toast('Приглашение скопировано.');return;}
    if(a==='accountOptions')options=await CtrlLive.api('/me/options');
    if(a==='news')await load();
    if(a==='community'){await load();data.me=(await CtrlLive.api('/me')).id;}
    if(a==='joinRoom'){await CtrlLive.api('/community/'+active.id+'/join','POST',{});await load();a='room';id=active.id;}
    if(a==='room'){active=(data.rooms||[]).find(r=>r.id===id);if(!active)return;data.me=(await CtrlLive.api('/me')).id;posts=active.joined?await CtrlLive.api('/community/'+id+'/posts'):[];if(active.kind==='group'){for(const p of posts)p.body=await CtrlPrivate.opened(id,{body:p.body,sender:p.sender});}}
    S.modal=a;render();if(a==='room')pollRoom(id);
  }
  function action(a,d){if(!modes.has(a)&&!['copyInvite','joinRoom','ticketStatus','roomReport','roomMute'].includes(a))return false;runApi(()=>open(a,d.id));return true;}
  async function form(el){
    const name=el.dataset.form;if(!['helpDesk','accountOptions','roomCreate','story','roomPost','roomJoin'].includes(name))return false;
    const btn=el.querySelector('button');if(btn.disabled)return true;btn.disabled=true;
    try{const v=Object.fromEntries(new FormData(el));const api=CtrlLive.api;
      if(name==='helpDesk'){ticket=await api('/support','POST',{email:v.email,body:v.text});sessionStorage.setItem('ctrl-help-ticket',JSON.stringify(ticket));reply='Обращение принято.';render();}
      else if(name==='accountOptions'){await api('/me/options','POST',{fee:Number(v.fee),frame:v.frame});S.clicks=(await api('/me')).clicks;S.modal='';render();}
      else if(name==='roomCreate'){const x=await api('/community/rooms','POST',v);await load();await open('room',x.id);}
      else if(name==='roomJoin'){await api('/community/'+v.id+'/join','POST',{});await load();await open('room',v.id);}
      else if(name==='roomPost'&&active.kind==='group'){const out=await CtrlPrivate.seal(active.id,null,v.text,'group');await api('/community/group-message','POST',{room:active.id,body:out.body,clientId:out.clientId});await CtrlPrivate.sent(active.id,out.clientId);await open('room',active.id);}
      else{const x=await api('/community/publish','POST',{kind:name==='story'?'story':'post',room:name==='story'?null:active.id,text:v.text});if(x.pending)toast('Публикация передана на проверку.');await open(name==='story'?'community':'room',active?.id);}
    }catch(e){toast(e.message);}finally{btn.disabled=false;}return true;
  }
  return {action,form,modal,clear(){clearTimeout(roomTimer);data={};active=null;posts=[];options={};reply='';}};
})();

