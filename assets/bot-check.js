const CtrlBot=(()=>{
  let load;
  async function check(site){
    if(!site)return '';
    if(!window.turnstile)await (load ||= new Promise((resolve,reject)=>{
      const s=document.createElement('script');s.src='https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit';s.onload=resolve;s.onerror=()=>{load=null;reject(Error('Не удалось загрузить проверку входа.'));};document.head.append(s);
    }));
    return new Promise((resolve,reject)=>{
      const d=document.createElement('dialog'),box=document.createElement('div'),cancel=document.createElement('button');
      cancel.className='key';cancel.textContent='Отмена';d.append(box,cancel);document.body.append(d);d.showModal();
      let id,done=false;function finish(token){if(done)return;done=true;if(id!=null)window.turnstile.remove(id);d.close();d.remove();token?resolve(token):reject(Error('Проверка входа не завершена.'));}
      cancel.onclick=()=>finish();d.oncancel=e=>{e.preventDefault();finish();};
      id=window.turnstile.render(box,{sitekey:site,theme:'dark',action:'ctrl_access',callback:finish,'error-callback':()=>finish(),'expired-callback':()=>finish()});
    });
  }
  return {check};
})();

