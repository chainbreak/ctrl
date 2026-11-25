import {Address,toNano} from '@ton/core';
import {keyPairFromSeed} from '@ton/crypto';
import {readFile} from 'node:fs/promises';
import protocol from './protocol.cjs';
import {check,parseWallet,sha256} from './proof.mjs';
const names={'first-gift':'Облачко Cloudflare','webm-gift-1':'Ctrl · Edition 2','webm-gift-2':'Ctrl · Edition 3'};
export async function giftVoucher(chain,cfg,data){
  check(cfg.upgradeCollection&&process.env.CTRL_VOUCHER_KEY_FILE,503,'Выпуск NFT-подарков ещё не подключён.');
  check(Object.hasOwn(names,data.gift)&&/^[a-f0-9]{32}$/.test(data.operation),400,'Проверьте подарок.');
  const shop=Address.parse(cfg.upgradeCollection),owner=parseWallet(data.address);
  const seed=await readFile(process.env.CTRL_VOUCHER_KEY_FILE);check(seed.length===32,503,'Проверьте ключ выпуска.');
  const keys=keyPairFromSeed(seed);seed.fill(0);
  try {
    const seq=await chain.snapshot();const signer=(await chain.get(seq,shop,'get_voucher_key')).readBigNumber();
    check(signer===BigInt('0x'+Buffer.from(keys.publicKey).toString('hex')),503,'Ключ выпуска не соответствует коллекции.');
    const index=BigInt('0x'+sha256([data.uid,data.operation,cfg.network,shop.toRawString()].join('|')).toString('hex'));
    const arg=[{type:'int',value:index}],item=(await chain.get(seq,shop,'get_nft_address_by_index',arg)).readAddress();
    const active=(await chain.rpc.getAccountLite(seq,item)).account.state.type==='active';
    if(active){const nft=await chain.get(seq,item,'get_nft_data');if(nft.readBoolean()&&nft.readBigNumber()===index&&nft.readAddress().equals(shop))return {minted:true,item:item.toRawString()};}
    return {minted:false,item:item.toRawString(),transaction:{validUntil:Math.floor(Date.now()/1000)+240,network:cfg.network,from:owner.toRawString(),messages:[{
      address:shop.toString({testOnly:cfg.network==='-3'}),amount:toNano('0.14').toString(),payload:protocol.voucher(shop,index,owner,names[data.gift],keys.secretKey).toBoc().toString('base64')
    }]}};
  } finally {keys.secretKey.fill(0);}
}

