import {Address,Cell,beginCell,toNano,fromNano} from '@ton/core';
import protocol from './protocol.cjs';
import {check,parseWallet,checkProof,verifySignature} from './proof.mjs';

export async function walletOperation(chain,config,method,data){
  const owner=parseWallet(data.address),seq=await chain.snapshot();
  if(method==='link-wallet'){
    check(data.network===config.network);checkProof(data.proof,new URL(config.publicOrigin).host,Math.floor(Date.now()/1000));
    verifySignature(owner,data.proof,await chain.key(seq,owner));return {address:owner.toRawString()};
  }
  const state=(await chain.rpc.getAccount(seq,chain.collection)).account.state;
  check(state.type==='active',503,'Коллекция не активна.');
  const cfg=Cell.fromBase64(state.data).beginParse();cfg.loadAddress();const master=cfg.loadAddress();
  const userWallet=(await chain.get(seq,master,'get_wallet_address',[protocol.argAddress(owner)])).readAddress();
  const info=await chain.rpc.getAccountLite(seq,userWallet);
  let balance=0n;
  if(info.account.state.type==='active'){
    const jetton=await chain.get(seq,userWallet,'get_wallet_data');balance=jetton.readBigNumber();
    check(jetton.readAddress().equals(owner)&&jetton.readAddress().equals(master),409,'Не удалось подтвердить кошелёк TAP.');
  }
  if(method==='wallet')return {address:owner.toRawString(),balance:fromNano(balance),network:config.network};
  const recipient=parseWallet(data.recipient);check(typeof data.amount==='string'&&/^\d{1,15}(\.\d{1,9})?$/.test(data.amount),400,'Укажите сумму TAP.');
  const amount=toNano(data.amount);check(amount>0n&&amount<=balance,409,'Проверьте сумму и баланс.');
  const transfer=beginCell().storeUint(0x0f8a7ea5,32).storeUint(0,64).storeCoins(amount).storeAddress(recipient).storeAddress(owner).storeBit(false).storeCoins(1n).storeBit(false).endCell();
  return {validUntil:Math.floor(Date.now()/1000)+240,network:config.network,from:owner.toRawString(),messages:[{address:userWallet.toString({testOnly:config.network==='-3'}),amount:toNano('0.12').toString(),payload:transfer.toBoc().toString('base64')}]};
}

