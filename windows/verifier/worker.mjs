import {walletOperation} from './wallet.mjs';
import {giftVoucher} from './vouchers.mjs';
import {prepareOrder} from './orders.mjs';
import { createInterface } from 'node:readline';
import { readFileSync, writeFileSync, linkSync, unlinkSync } from 'node:fs';
import { randomBytes, randomUUID } from 'node:crypto';
import { join } from 'node:path';
import { Auth } from './auth.mjs';
import {Address} from '@ton/core';
import protocol from './protocol.cjs';
import { TonChain } from './chain.mjs';
import { canonicalNumber, check, sha256 } from './proof.mjs';

// Only the parent process can send requests over this pipe.
const config = JSON.parse(process.env.CTRL_TON_WORKER_CONFIG);
const root = process.env.CTRL_TON_STORAGE;
function signingKey() {
  const keyFile = join(root,'ton-signing.key');
  try { return readFileSync(keyFile); } catch (e) { if (e.code !== 'ENOENT') throw e; }
  const temp = keyFile+'.'+randomUUID();
  writeFileSync(temp,randomBytes(32),{mode:0o600,flag:'wx'});
  try { linkSync(temp,keyFile); } catch (e) { if(e.code!=='EEXIST') throw e; } finally { unlinkSync(temp); }
  return readFileSync(keyFile);
}
const chain = new TonChain(config.rpcEndpoint,config.collection);
const auth = new Auth({file:join(root,'ton-sessions.sqlite'),chain,domain:new URL(config.publicOrigin).host,
  network:config.network,secret:signingKey()});
async function dispatch(method, data) {
  check(data && typeof data === 'object' && !Array.isArray(data),400,'Проверьте данные запроса.');
  switch (method) {
    case 'handle':{
      check(config.usernameCollection,503,'Коллекция юзернеймов ещё не настроена.');
      const collection=Address.parse(config.usernameCollection),index=protocol.usernameIndex(data.handle),seq=await chain.snapshot();
      const item=(await chain.get(seq,collection,'get_nft_address_by_index',[{type:'int',value:index}])).readAddress();
      if((await chain.rpc.getAccountLite(seq,item)).account.state.type!=='active')return {};
      const nft=await chain.get(seq,item,'get_nft_data');if(!nft.readBoolean()||nft.readBigNumber()!==index||!nft.readAddress().equals(collection))return {};
      const wallet=nft.readAddress();const version=(await chain.get(seq,item,'get_ownership_version')).readBigNumber();
      return {wallet:wallet.toRawString(),item:item.toRawString(),version:version.toString()};
    }
    case 'voucher':return giftVoucher(chain,config,data);
    case 'link-wallet':case 'wallet':case 'transfer':return walletOperation(chain,config,method,data);
    case 'order': return prepareOrder(chain,config,data);
    case 'challenge': return auth.challenge(data.number);
    case 'login': {
      const session = await auth.login(data);
      const row = await auth.session(session.token);
      return {...session,wallet:row.wallet,subject:row.subject,privateSubject:sha256([row.subject,row.wallet].join('|')).toString('hex')};
    }
    case 'me': return auth.me(data.token);
    case 'logout': await auth.logout(data.token); return {};
    case 'lookup': {
      const number = canonicalNumber(data.number);
      const owner = await auth.rpc(async()=>chain.owner(await chain.snapshot(),number));
      if (!owner) return {};
      const subject = sha256([auth.issuer,owner.item].join('|')).toString('hex');
      return {privateSubject:sha256([subject,owner.wallet].join('|')).toString('hex')};
    }
    default: throw Error('Unknown operation');
  }
}
let sweeping=false;
const timer=setInterval(async()=>{
  if(sweeping)return; sweeping=true;
  try{await auth.sweep()}catch{}finally{sweeping=false}
},30000);
const input=createInterface({input:process.stdin});
input.on('line', async line=>{
  let id;
  try {
    const request=JSON.parse(line); id=request.id;
    const data=await dispatch(request.method,request.data);
    process.stdout.write(JSON.stringify({id,status:200,data})+'\n');
  } catch(e) {
    process.stdout.write(JSON.stringify({id,status:e.status||503,data:{error:e.status?e.message:'Проверка номера временно недоступна.'}})+'\n');
  }
});
input.on('close',()=>{clearInterval(timer);process.exit(0)});

