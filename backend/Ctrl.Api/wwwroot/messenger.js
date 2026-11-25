const desk=document.getElementById('messenger-desk');
const output=document.getElementById('messenger-results');
const command=(kind,target='',amount=0,note='')=>api('/admin/messenger',{method:'POST',body:JSON.stringify({kind,target,amount,note,operation:crypto.randomUUID()})});
function row(title,sub=''){const div=document.createElement('article');div.className='panel';const h=document.createElement('b');h.textContent=title;const p=document.createElement('p');p.textContent=sub;div.append(h,p);output.append(div);return div;}
function control(card,title,fn){card.append(button(title,async()=>{try{await fn();}catch(e){showNotice(e.message,'error');}}));}
async function queue(){
  const q=await command('queue');output.replaceChildren();
  for(const t of q.tickets){const card=row(t.email,t.body);control(card,'Ответить',async()=>{const note=prompt('Ответ службы поддержки');if(note){await command('reply',t.id,0,note);await queue();}});}
  for(const t of q.complaints){const card=row(t.reason,t.evidence);control(card,'Закрыть жалобу',async()=>{await command('dismiss-report',t.id);await queue();});}
  for(const t of q.publications){const card=row('Публикация',t.body);for(const [kind,title] of [['approve-post','Опубликовать'],['reject-post','Отклонить']])control(card,title,async()=>{await command(kind,String(t.id));await queue();});}
  for(const t of q.noer){const card=row('NOER · '+t.target,t.state==='paid'?'Оплачено':'Ожидает оплаты');
    control(card,'Документ и участники',async()=>{const r=await command('document',t.id);const a=document.createElement('a');a.textContent='Скачать документ';const url=URL.createObjectURL(new Blob([Uint8Array.from(atob(r.data),c=>c.charCodeAt(0))],{type:'application/octet-stream'}));a.href=url;a.download='NOER-'+t.id+({'image/png':'.png','image/jpeg':'.jpg','application/pdf':'.pdf'}[r.mime]||'');card.append(a);setTimeout(()=>URL.revokeObjectURL(url),90000);const p=document.createElement('p');p.textContent=r.members.map(x=>'@'+x.username).join(', ');card.append(p);});
    if(t.state==='paid')control(card,'Подтвердить NOER',async()=>{await command('approve-noer',t.id);await queue();});
    control(card,'Отклонить',async()=>{await command('reject-noer',t.id);await queue();});}
  if(!output.children.length)row('Очередь пуста');
}
async function search(){
  const list=await command('users',desk.elements.q.value.trim().replace(/^@/,''));output.replaceChildren();
  for(const u of list){const card=row('@'+u.username,u.name+' · '+u.clicks+' Кликов');if(!document.getElementById('search-form').hidden)control(card,'Назначить служебное имя',async()=>{const handle=prompt('Три латинские буквы');if(handle){await command('staff-handle',u.id,0,handle);await search();}});control(card,'Изменить Клики',async()=>{const v=prompt('Изменение баланса, например 50 или −50');if(v&&Number.isSafeInteger(Number(v))){await command('clicks',u.id,Number(v));await search();}});
    control(card,u.premium?'Снять Premium':'Выдать Premium',async()=>{await command('premium',u.id,u.premium?0:1);await search();});
    control(card,u.banned?'Снять блокировку':'Заблокировать',async()=>{if(confirm('Изменить доступ @'+u.username+'?')){await command('ban',u.id,u.banned?0:1);await search();}});}
  if(!list.length)row('Пользователь не найден');
}
desk.onsubmit=e=>{e.preventDefault();search().catch(e=>showNotice(e.message,'error'));};
document.getElementById('messenger-queue').onclick=()=>queue().catch(e=>showNotice(e.message,'error'));

