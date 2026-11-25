const CtrlMotion = (() => {
  const media = matchMedia('(prefers-reduced-motion: reduce)');
  const running = new Map();
  let route = '', modal = '', conversation = '';
  function animate(el, distance, duration = 190) {
    if (!el || media.matches || !el.animate) return;
    running.get(el)?.cancel();
    const anim = el.animate([{opacity:0,transform:'translateY('+distance+'px)'},{opacity:1,transform:'translateY(0)'}],
      {duration,easing:'cubic-bezier(.2,.72,.22,1)'});
    running.set(el,anim);
    anim.finished.catch(()=>{}).finally(()=>{if(running.get(el)===anim)running.delete(el)});
  }
  media.addEventListener('change',()=>{if(media.matches)for(const animation of running.values())animation.cancel()});
  function patch(element, html) {
    const next=element.cloneNode(false);
    next.innerHTML=html;
    morphdom(element,next,{childrenOnly:true,
      getNodeKey:node=>node.id || node.dataset?.motionId || undefined,
      onBeforeElUpdated(from,to){
        if(from.tagName==='DETAILS')to.open=from.open;
        if(from.id==='person-results' || from.id==='gift-animation' || from.tagName==='AUDIO')return false;
        if(from.matches('input,textarea') && document.activeElement===from && from.name===to.name &&
           from.type===to.type && from.id!=='inp')to.value=from.value;
        return !from.isEqualNode(to);
      }
    });
  }
  function frame(main, mainHtml, modalHtml, keys) {
    const box=main.querySelector('#msgs'), pinned=!box || box.scrollHeight-box.scrollTop-box.clientHeight<48;
    const previousTop=box?.scrollTop || 0;
    const previousMainTop=main.querySelector('.pg')?.scrollTop || 0;
    const known=new Set([...main.querySelectorAll('[data-msg]')].map(el=>el.dataset.msg));
    const holder=document.getElementById('modal');
    patch(main,mainHtml); patch(holder,modalHtml);
    const messages=main.querySelector('#msgs');
    if(messages)messages.scrollTop=keys.chat!==conversation || pinned ? messages.scrollHeight : previousTop;
    const page=main.querySelector('.pg');
    if(page)page.scrollTop=keys.page===route ? previousMainTop : 0;
    if(keys.page!==route)animate(main.firstElementChild,7,210);
    else if(keys.chat!==conversation)animate(main.querySelector('.cw'),4);
    else if(known.size)for(const row of [...main.querySelectorAll('[data-msg]')].slice(-3))if(!known.has(row.dataset.msg))animate(row,4,150);
    if(keys.modal && keys.modal!==modal){animate(holder.querySelector('.dlg'),8,170);holder.querySelector('[autofocus]')?.focus();}
    route=keys.page;conversation=keys.chat;modal=keys.modal;
  }
  return {frame,patch};
})();

