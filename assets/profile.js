const CtrlProfile=(()=>{
  const fields=['profileAvatar','profileBackground','profileBanner','chatBanner'];let peer;
  const uri=v=>typeof v==='string'&&/^data:image\/(?:webp|gif);base64,[A-Za-z0-9+/]+=*$/.test(v)?v:'';
  function clear(){fields.forEach(f=>S[f]='');peer=null;}
  async function load(){const art=await CtrlLive.api('/me/artwork');clear();for(const a of art){if(fields.includes(a.field))S[a.field]=uri(a.data);}}
  async function save(field,data){if(fields.includes(field))await CtrlLive.api('/me/artwork','POST',{field,data});}
  async function image(file,target){
    if(['profileBanner','chatBanner'].includes(target)&&file.type==='image/gif'){
      if(!S.premium)throw Error('Для анимированного баннера нужен Premium.');if(file.size>1180000)throw Error('Выберите GIF до 1,1 МБ.');
      const data=await new Promise((res,rej)=>{const f=new FileReader();f.onload=()=>res(f.result);f.onerror=()=>rej(Error('Не удалось прочитать GIF.'));f.readAsDataURL(file);});return {data};
    }return normalizeImage(file);
  }
  function action(a,d){if(a==='clearImage'&&fields.includes(d.id)){runApi(async()=>{await save(d.id,'');S[d.id]='';render();});return true;}
    if(a==='peerProfile'){const c=cur();if(c?.peerId)runApi(async()=>{peer=await CtrlLive.api('/profile/'+encodeURIComponent(c.peerId));if(peer.user.publicBalance)try{peer.balance=(await CtrlLive.api('/users/'+encodeURIComponent(c.peerId)+'/wallet')).balance;}catch{}S.modal='peerProfile';render();});return true;}return false;}
  function modal(){if(S.modal!=='peerProfile'||!peer)return '';const u=peer.user,a=Object.fromEntries(peer.art.map(x=>[x.field,uri(x.data)]));const img=(k,cls)=>a[k]?'<img class="'+cls+'" src="'+a[k]+'" alt="">':'';
    return '<div class="mod" data-a="close"><section data-stop class="dlg" role="dialog">'+img('profileBanner','peer-cover')+img('profileAvatar','peer-avatar frame-'+esc(u.frame||''))+'<h2>'+esc(u.name)+'</h2><p>@'+esc(u.username)+(u.verified?' · NOER ✓':'')+(u.premium?' · Ctrl+':'')+'</p><p>'+esc(u.bio||'')+'</p>'+(peer.balance!=null?'<p>'+esc(peer.balance)+' TAP</p>':'')+peer.stories.map(t=>'<article class="card"><small>История</small><p>'+esc(t.body)+'</p></article>').join('')+key('close','Закрыть','sm')+'</section></div>';}
  return {load,save,image,action,clear,modal};
})();

