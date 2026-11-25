(async function () {
  const status = document.querySelector('[role=status]'), form = document.querySelector('form');
  const connect = form.querySelector('button'), logout = document.querySelector('#logout');
  let token, number, verifying = false;
  async function api(url, data, method = 'POST') {
    const response = await fetch(url, {method, headers:{'Content-Type':'application/json', ...(token ? {Authorization:'Bearer '+token} : {})},
      body:data ? JSON.stringify(data) : undefined});
    const result = await response.json();
    if (!response.ok) throw Object.assign(new Error(result.error), {status:response.status});
    return result;
  }
  try {
    const config = await api('/config', null, 'GET');
    const ui = new TON_CONNECT_UI.TonConnectUI({manifestUrl:config.origin+'/tonconnect-manifest.json',restoreConnection:false});
    ui.setConnectionNetwork(config.network);
    form.onsubmit = async e => {
      e.preventDefault(); connect.disabled = true; status.textContent = '';
      try {
        if (ui.connected) await ui.disconnect();
        number = document.querySelector('#number').value;
        const {payload} = await api('/auth/challenge',{number});
        ui.setConnectRequestParameters({state:'ready', value:{tonProof:payload}});
        await ui.openModal();
      } catch (error) { status.textContent = error.message; }
      finally { connect.disabled = false; }
    };
    ui.onStatusChange(async wallet => {
      if (!wallet || verifying) return;
      verifying = true;
      try {
        const proof = wallet.connectItems?.tonProof?.proof;
        if (!proof) throw Error('Кошелёк не подтвердил подпись. Подключитесь ещё раз.');
        const session = await api('/auth/verify',{address:wallet.account.address,network:wallet.account.chain,number,proof});
        token = session.token;
        const user = await api('/auth/me',null,'GET');
        document.querySelector('h1').textContent = user.number;
        status.textContent = 'Вы вошли в Ctrl'; form.hidden = true; logout.hidden = false;
      } catch (error) { status.textContent = error.message; }
      finally { verifying = false; }
    });
    function signedOut(text) {
      token = null; form.hidden = false; logout.hidden = true; document.querySelector('h1').textContent = 'Вход по номеру'; status.textContent = text;
    }
    logout.onclick = async () => {
      try { await api('/auth/logout'); signedOut(''); if (ui.connected) await ui.disconnect(); }
      catch (error) { if (error.status === 401) signedOut(error.message); else status.textContent = error.message; }
    };
    setInterval(async () => {
      if (!token || !form.hidden) return;
      try { await api('/auth/me',null,'GET'); status.textContent = 'Вы вошли в Ctrl'; }
      catch (error) { if (error.status === 401) signedOut(error.message); else status.textContent = error.message; }
    },30000);
  } catch { status.textContent = 'Не удалось открыть вход. Обновите страницу.'; connect.disabled = true; }
})();

