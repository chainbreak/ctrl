import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { mkdir, mkdtemp, writeFile, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { randomBytes, randomUUID } from 'node:crypto';
import { once } from 'node:events';
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { setTimeout as sleep } from 'node:timers/promises';
import { proofDigest } from '../verifier/proof.mjs';

const blueprint=resolve(process.env.CTRL_BLUEPRINT || '../ton');
const dep=createRequire(join(blueprint,'package.json'));
const {Address,Cell,internal,toNano,SendMode,parseTuple,serializeTuple,beginCell,storeStateInit}=dep('@ton/core');
const {Blockchain}=dep('@ton/sandbox'), {WalletContractV4}=dep('@ton/ton');
const {keyPairFromSeed,sign}=dep('@ton/crypto');
const p=dep(join(blueprint,'wrappers/protocol.cjs'));
const {chromium}=createRequire(resolve(process.env.CTRL_PLAYWRIGHT || '../ctrl-ui-test/package.json'))('playwright-core');

async function listen(server) { server.listen(0,'127.0.0.1'); await once(server,'listening'); return server.address().port; }
async function freePort(){const server=createServer(); const port=await listen(server); await new Promise(done=>server.close(done));return port;}
const temp=await mkdtemp(join(tmpdir(),'ctrl-26-check-'));
const chain=await Blockchain.create(), treasury=await chain.treasury('ctrl');
const tap=p.master(treasury.address), numbers=p.collection(treasury.address,tap.address,false,toNano('10'));
for(const c of [tap,numbers])await treasury.send({to:c.address,init:c.init,value:toNano('1')});
async function wallet() {
    const pair=keyPairFromSeed(randomBytes(32)), w=chain.openContract(WalletContractV4.create({workchain:0,publicKey:pair.publicKey}));
    await treasury.send({to:w.address,value:toNano('2'),bounce:false});
    const send=async(to,body)=>w.sendTransfer({seqno:await w.getSeqno(),secretKey:pair.secretKey,sendMode:SendMode.PAY_GAS_SEPARATELY,messages:[internal({to,body,value:toNano('0.2'),bounce:false})]});
    await send(treasury.address);return {pair,w,send};
}
const alice=await wallet(), next=await wallet(), number='+33309000001',domain='ctrl.example.org';
await treasury.send({to:tap.address,value:toNano('0.3'),body:p.mint(alice.w.address,toNano('10'))});
const getter=async(a,method,args)=>(await chain.runGetMethod(a,method,args)).stackReader;
const aw=(await getter(tap.address,'get_wallet_address',[p.argAddress(alice.w.address)])).readAddress();
await alice.send(aw,p.deposit(numbers.address,alice.w.address,toNano('10')));
await alice.send(numbers.address,p.buy(number));
const item=(await getter(numbers.address,'get_nft_address_by_index',[{type:'int',value:p.numberIndex(number)}])).readAddress();
let height=1, offline=false;
const rpc=createServer(async(req,res)=>{
    try {
        if(offline)throw Error('offline');
        const fields=new URL(req.url,'http://localhost').pathname.slice(1).split('/');
        const block={workchain:-1,seqno:height,shard:'0',rootHash:'x',fileHash:'y'};let result;
        if(fields[1]==='latest')result={last:block,init:block,stateRootHash:'z',now:Math.floor(Date.now()/1000)};
        else {
            const address=Address.parse(fields[2]);
            if(fields[3]==='run'){
                const args=fields[5]?parseTuple(Cell.fromBoc(Buffer.from(fields[5],'base64url'))[0]):[];
                const run=await chain.runGetMethod(address,fields[4],args);
                result={block,shardBlock:block,exitCode:run.exitCode,resultRaw:serializeTuple(run.stack).toBoc().toString('base64')};
            } else {
                const state=(await chain.getContract(address)).accountState;
                const active=state?.type==='active';
                result={block,account:{balance:{coins:'1',currencies:{}},last:null,storageStat:null,state:active?
                    (fields[3]==='lite'?{type:'active',codeHash:'x',dataHash:'y'}:{type:'active',code:state.state.code.toBoc().toString('base64'),data:state.state.data.toBoc().toString('base64')}):{type:'uninit'}}};
            }
        }
        res.writeHead(200,{'Content-Type':'application/json'});res.end(JSON.stringify(result));
    } catch {res.writeHead(503);res.end();}
});
const rpcPort=await listen(rpc), port=await freePort(), debug=await freePort();
const base='http://127.0.0.1:'+port, root=join(temp,'profile');
await mkdir(root);await writeFile(join(root,'server.txt'),base);
const data=join(temp,'server');await mkdir(data);
const flags={windowsHide:true,stdio:['ignore','ignore','inherit']};
const staffSecret=randomBytes(32).toString("hex");
const startMessenger=()=>spawn(resolve('publish-server/ctrl-server.exe'),[],{...flags,env:{...process.env,CTRL_DATA:data,CTRL_LISTEN:base,CTRL_MANAGEMENT_KEY:staffSecret,CTRL_REVIEW_TERMS:"ctrl-qa-review"}});
let messenger=startMessenger();
let app,browser,page,peerBrowser,peerPage;
async function api(path,method='GET',body,token,status=200) {
    const res=await fetch(base+path,{method,headers:{'Content-Type':'application/json',...(token?{Authorization:'Bearer '+token}:{})},body:body?JSON.stringify(body):undefined});
    const result=await res.text(); assert.equal(res.status,status,path+' '+result.slice(0,120));
    return result?JSON.parse(result):null;
}
function signProof(w,payload) {
    const timestamp=Math.floor(Date.now()/1000);
    return {payload,timestamp,domain:{value:domain,lengthBytes:domain.length},signature:sign(proofDigest(w.w.address,domain,timestamp,payload),w.pair.secretKey).toString('base64')};
}
async function login(w,status=200) {
    const {payload}=await api('/api/ton/challenge','POST',{number});
    return api('/api/ton/login','POST',{number,address:w.w.address.toRawString(),network:'-3',proof:signProof(w,payload)},null,status);
}
try {
    for(let i=0;i<80;i++){try{await api('/health');break;}catch{await sleep(250)}}
    await api('/api/ton/config','GET',null,null,503);
    await writeFile(join(data,'ctrl-ton.json'),JSON.stringify({publicOrigin:'https://'+domain,network:'-3',collection:numbers.address.toRawString(),rpcEndpoint:'http://127.0.0.1:'+rpcPort}));
    const cfg=await api('/api/ton/config');assert.equal(cfg.network,'-3');
    const bob=await api('/api/register','POST',{name:'Борис',username:'borisctrl',email:'ctrltest_boris@gmail.com',password:'GoodTestPassword312'});
    const again=await api('/api/login','POST',{loginName:'borisctrl',password:'GoodTestPassword312'});
    assert.equal(bob.user.id,again.user.id);
    const staff=async(kind,target,amount=0,note='',operation=randomUUID())=>{
      const res=await fetch(base+'/management',{method:'POST',headers:{'Content-Type':'application/json','X-Ctrl-Management':staffSecret},body:JSON.stringify({actor:'qa',kind,target,amount,note,operation})});
      assert.equal(res.status,200,kind);return res.json();
    };
    const same=randomUUID();await staff('clicks',bob.user.id,100,'',same);await staff('clicks',bob.user.id,100,'',same);
    assert.equal((await api('/api/me','GET',null,bob.token)).clicks,100);
    await api('/api/me/options','POST',{fee:0,frame:'silver'},bob.token);
    assert.equal((await api('/api/me','GET',null,bob.token)).clicks,75);
    const room=await api('/api/community/rooms','POST',{kind:'channel',title:'Новости'},bob.token);
    const pending=await api('/api/community/publish','POST',{kind:'post',room:room.id,text:'ctrl-qa-review'},bob.token);
    assert.ok(pending.pending);const queue=await staff('queue','');await staff('approve-post',String(queue.publications[0].id));
    assert.equal((await api('/api/community/'+room.id+'/posts','GET',null,bob.token)).length,1);
    const ticket=await api('/api/support','POST',{body:'Помощь',email:'ticket@example.org'});await staff('reply',ticket.id,0,'Ответ готов');
    assert.equal((await api('/api/support/status','POST',ticket)).answer,'Ответ готов');
    const noer=await api('/api/noer','POST',{room:room.id,document:Buffer.from('%PDF-1.5 synthetic QA fixture').toString('base64')},bob.token);
    assert.equal((await staff('document',noer.id)).members[0].username,'borisctrl');
    await staff('reject-noer',noer.id);
    const session=await login(alice); assert.equal(session.user.number,number);
    await login(next,403);
    const found=await api('/api/users?q='+encodeURIComponent(number),'GET',null,bob.token);assert.equal(found[0].id,session.user.id);
    const chat=await api('/api/conversations','POST',{peerId:bob.user.id},session.token);
    const messages='/api/conversations/'+chat.id+'/messages';
    await api(messages,'POST',{clientId:randomUUID(),body:'legacy plaintext'},session.token,428);
    peerBrowser=await chromium.launch({channel:'chrome',headless:true,args:['--use-fake-device-for-media-stream','--use-fake-ui-for-media-stream','--autoplay-policy=no-user-gesture-required']});
    peerPage=await peerBrowser.newPage();
    peerPage.on('pageerror',e=>console.error('peer error',e.message));
    await peerPage.goto(base);
    await peerPage.locator('[name=email]').fill('borisctrl');
    await peerPage.locator('[name=password]').fill('GoodTestPassword312');
    await peerPage.locator('[data-form=desktop-auth] button').click();
    await peerPage.locator('[data-a=chat]').first().waitFor({timeout:25000});
    await peerPage.locator('[data-a=chat]').first().click();
    console.log('PASS published server, bundled verifier and peer device registration');
    app=spawn(resolve('publish-client/ctrl.exe'),[],{...flags,env:{...process.env,CTRL_CLIENT_DATA:root,WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS:'--use-fake-device-for-media-stream --use-fake-ui-for-media-stream --autoplay-policy=no-user-gesture-required --remote-debugging-port='+debug}});
    for(let i=0;i<120;i++){try{browser=await chromium.connectOverCDP('http://127.0.0.1:'+debug,{timeout:800});break;}catch{await sleep(250)}}
    assert.ok(browser,'WebView2 startup');
    for(let i=0;i<80;i++){page=browser.contexts()[0]?.pages().find(p=>p.url().includes('ctrl.local'));if(page)break;await sleep(200)}
    assert.ok(page,'Ctrl page');
    if(process.env.CTRL_QA_SHOTS){await mkdir(process.env.CTRL_QA_SHOTS,{recursive:true});await page.locator('[data-form=desktop-auth]').waitFor();await page.screenshot({path:join(process.env.CTRL_QA_SHOTS,'login.png')});}
    const walletInfo={name:'Ctrl QA Wallet',app_name:'ctrlqawallet',image:'https://ctrl.local/ton-icon.png',about_url:'https://ctrl.example.org',platforms:['windows']};
    await page.route(/wallets.*\.json/, route=>route.fulfill({contentType:'application/json',headers:{'Access-Control-Allow-Origin':'*'},body:JSON.stringify([{...walletInfo,bridge:[{type:'js',key:'ctrlqawallet'}]}])}));
    await page.exposeFunction('confirmTestWallet',request=>{
        const payload=request.items.find(i=>i.name==='ton_proof').payload;
        assert.equal(request.manifestUrl,cfg.manifestUrl);
        return {event:'connect',id:1,payload:{device:{platform:'windows',appName:'ctrlqawallet',appVersion:'1.0',maxProtocolVersion:2,features:['SendTransaction']},items:[
            {name:'ton_addr',address:alice.w.address.toRawString(),network:'-3',publicKey:alice.pair.publicKey.toString('hex'),walletStateInit:beginCell().store(storeStateInit(alice.w.init)).endCell().toBoc().toString('base64')},
            {name:'ton_proof',proof:signProof(alice,payload)}]}};
    });
    await page.evaluate(info=>{window.ctrlqawallet={tonconnect:{walletInfo:info,deviceInfo:{platform:'windows',appName:'ctrlqawallet',appVersion:'1.0',maxProtocolVersion:2,features:['SendTransaction']},protocolVersion:2,isWalletBrowser:true,connect:(_,request)=>window.confirmTestWallet(request),listen:()=>()=>{},disconnect:async()=>{},send:async()=>({}),restoreConnection:async()=>({event:'connect_error',id:0,payload:{code:100,message:'Manual login'}})}}},walletInfo);
    await page.locator('[data-a=authMode][data-id=nft]').click();await page.locator('input[name=number]').fill(number);await page.locator('[data-form=desktop-auth] button').click();
    const pick=page.getByText('Ctrl QA Wallet',{exact:false}).first();
    if (!(await pick.isVisible())) {
        const more=page.getByText(/View all|Все кошельки|Показать все/i).first();
        try {await more.click({timeout:3000})} catch {}
    }
    try{await pick.click({timeout:5000})}catch{}
    await page.locator('[data-a=tab][data-id=me]').waitFor({state:'visible',timeout:22000});
    await page.locator('.inbox-head [data-a=newchat]').click();
    await page.locator('input[name=handle]').fill('borisc');
    await page.locator('[data-a=startChat]').first().click();
    const compact=()=>api('/api/conversations?compact=true','GET',null,bob.token);
    const cursor=(await api('/api/inbox/changes','GET',null,bob.token)).cursor;
    const since='/api/inbox/changes?since='+cursor;
    const devices=Promise.all([api(since,'GET',null,bob.token),api(since,'GET',null,bob.token)]);
    await page.locator('#inp').fill('Из приложения Ctrl');await page.locator('[data-a=send]').click();
    for(const update of await devices)assert.notEqual(update.cursor,cursor);
    assert.equal((await compact())[0].lastText,null);
    await page.getByText('Из приложения Ctrl',{exact:true}).last().waitFor({timeout:10000});
    const wire=(await api(messages,'GET',null,bob.token)).at(-1).body;
    assert.ok(wire.startsWith('SIG1:') && !wire.includes('Из приложения Ctrl'));
    await peerPage.getByText('Из приложения Ctrl',{exact:true}).last().waitFor({timeout:12000});
    await peerPage.locator('#inp').fill('Ответ Бориса');await peerPage.locator('[data-a=send]').click();
    await page.getByText('Ответ Бориса',{exact:true}).last().waitFor({timeout:15000});
    const sample=Buffer.alloc(1048576+96,83);
    await peerPage.evaluate(async bytes=>{const raw=new Uint8Array(bytes);await CtrlFiles.upload(new File([raw],'Ctrl QA.dat'));},Array.from(sample));
    await page.locator('[data-a=downloadFile]').waitFor({timeout:18000});
    const downloaded=await page.evaluate(async()=>{
      let output=[];window.showSaveFilePicker=async()=>({createWritable:async()=>({write:async b=>output.push(new Uint8Array(b)),close:async()=>{},abort:async()=>{}})});
      const id=document.querySelector('[data-a=downloadFile]').dataset.id;await CtrlFiles.download(id);const bytes=new Uint8Array(await new Blob(output).arrayBuffer());return {length:bytes.length,intact:bytes.every(x=>x===83)};
    });assert.deepEqual(downloaded,{length:sample.length,intact:true});
    const group=await api('/api/community/rooms','POST',{title:'Обсуждение',kind:'group'},bob.token);
    await api('/api/community/'+group.id+'/join','POST',{},session.token);
    await peerPage.evaluate(async id=>{const out=await CtrlPrivate.seal(id,null,'В группе','group');await CtrlLive.api('/community/group-message','POST',{room:id,body:out.body,clientId:out.clientId});await CtrlPrivate.sent(id,out.clientId);},group.id);
    assert.equal(await page.evaluate(async id=>{const [m]=await CtrlLive.api('/community/'+id+'/posts');return CtrlPrivate.opened(id,m);},group.id),'В группе');
    const bytes=Buffer.from('RIFF sample in a fixture');const track=await api('/api/music/tracks','POST',{id:randomUUID(),title:'Проверка',mime:'audio/wav',size:bytes.length,seconds:2},bob.token);
    await api('/api/music/'+track.id+'/parts/0','POST',{data:bytes.toString('base64')},bob.token);
    await api('/api/music/'+track.id+'/finish','POST',{},bob.token);
    const stream=await api('/api/music/'+track.id+'/listen','POST',{},bob.token);
    const range=await fetch(base+stream.path,{headers:{Range:'bytes=0-3'}});assert.equal(range.status,206);assert.equal(await range.text(),'RIFF');
    await api('/api/music/'+track.id+'/listen','POST',{},session.token,404);
    await api('/api/me/options','POST',{fee:4,frame:'silver'},bob.token);await staff('clicks',session.user.id,20);
    const paid=await page.evaluate(async()=>{const c=cur();const out=await CtrlPrivate.seal(c.id,c.peerId,'Платное сообщение');const req={clientId:out.clientId,body:out.body,acceptedFee:4};
      const one=await CtrlLive.api('/conversations/'+c.id+'/messages','POST',req);const two=await CtrlLive.api('/conversations/'+c.id+'/messages','POST',req);await CtrlPrivate.sent(c.id,out.clientId);return [one.seq,two.seq];});
    assert.equal(paid[0],paid[1]);assert.equal((await api('/api/me','GET',null,session.token)).clicks,16);assert.equal((await api('/api/me','GET',null,bob.token)).clicks,79);
    if(process.env.CTRL_QA_SHOTS)await peerPage.screenshot({path:join(process.env.CTRL_QA_SHOTS,'chat.png'),timeout:8000});
    console.log('PASS group encryption, paid message retry and ranged audio');
    console.log('PASS staff accounting, report queue, NOER evidence, support, encrypted multi-chunk file');
    await page.locator('[data-a=audioCall]').click();
    await peerPage.getByRole('button',{name:'Ответить',exact:true}).click({timeout:22000});
    await peerPage.getByText('На связи',{exact:true}).waitFor({timeout:25000});
    await page.getByText('На связи',{exact:true}).waitFor({timeout:15000});
    await page.getByRole('button',{name:'Завершить',exact:true}).click();
    await peerPage.waitForSelector('#ctrl-call',{state:'detached'});
    console.log('PASS encrypted WebRTC signaling and connected audio tracks');
    console.log('PASS Windows EXE, WebView2, TON Connect JS bridge, outgoing and incoming chat');
    await page.locator('[data-a=tab][data-id=me]').first().click();
    await page.locator('[data-a=tab][data-id=gifts]').first().click();
    await page.waitForSelector('#gift-animation svg');const giftNode=await page.locator('#gift-animation svg').elementHandle();
    await page.evaluate(()=>render());assert.ok(await giftNode.evaluate(node=>node.isConnected),'preserved Lottie player');
    await page.emulateMedia({reducedMotion:'reduce'});await page.locator('[data-a=tab][data-id=me]').first().click();
    assert.equal(await page.locator('#main').evaluate(el=>el.getAnimations({subtree:true}).filter(a=>a.playState==='running').length),0);
    await page.locator('[data-a=tab][data-id=chats]').click();
    console.log('PASS original gift player survives rerender and reduced-motion preference');
    offline=true;await api(messages,'GET',null,session.token,503);offline=false;
    await alice.send(item,p.transferItem(next.w.address,alice.w.address));height++;
    await api(messages,'GET',null,session.token,401);
    await page.locator('[data-form=desktop-auth]').waitFor({state:'visible',timeout:15000});
    const buyer=await login(next);
    assert.notEqual(buyer.user.id,session.user.id);
    await api(messages,'GET',null,buyer.token,404);
    assert.deepEqual(await api('/api/conversations','GET',null,buyer.token),[]);
    assert.equal((await api('/api/users?q='+encodeURIComponent(number),'GET',null,bob.token))[0].id,buyer.user.id);
    await api('/api/logout','POST',{},buyer.token);
    await api('/api/me','GET',null,buyer.token,401);
    console.log('PASS ownership transfer, revoked EXE, isolated chats, network outage, logout');
    const archived=await api(messages,'GET',null,bob.token);
    assert.ok(archived.length>1);
    const oldCursor=(await api('/api/inbox/changes','GET',null,bob.token)).cursor;
    const stopped=new Promise(resolve=>messenger.once('exit',resolve));
    messenger.kill();await stopped;messenger=startMessenger();
    let ready=false;
    for(let i=0;i<90;i++){try{if((await api('/health/ready')).ready){ready=true;break;}}catch{}await sleep(200);}
    assert.ok(ready,'independent service restarts');
    assert.notEqual((await api('/api/inbox/changes?since='+oldCursor,'GET',null,bob.token)).cursor,oldCursor);
    assert.equal((await api('/api/me','GET',null,bob.token)).id,bob.user.id);
    assert.deepEqual(await api(messages,'GET',null,bob.token),archived);
    console.log('PASS two waiting devices, compact inbox, authenticated session and exact encrypted history across backend restart');
} catch(e) {
    if(peerPage)console.error((await peerPage.locator("body").innerText()).slice(-1200));if(page)console.error((await page.locator('body').innerText()).slice(0,1200));
    throw e;
} finally {if(app)app.kill();messenger.kill();rpc.closeAllConnections();rpc.close();if(peerBrowser)await peerBrowser.close();if(browser)await browser.close().catch(()=>{});}

