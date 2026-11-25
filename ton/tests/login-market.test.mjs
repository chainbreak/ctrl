import test from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes } from 'node:crypto';
import { internal, toNano, SendMode } from '@ton/core';
import { WalletContractV4 } from '@ton/ton';
import { Blockchain } from '@ton/sandbox';
import { keyPairFromSeed, sign } from '@ton/crypto';
import protocol from '../wrappers/protocol.cjs';
import { Auth } from '../backend/auth.mjs';
import { TonChain } from '../backend/chain.mjs';
import { proofDigest } from '../backend/proof.mjs';

// Use the real adapter, replacing only the network transport with Blueprint's sandbox.
test('signed wallet → TAP purchase → sign-in → transfer NFT → switch account holder', async t => {
  const chain = await Blockchain.create();
  const treasury = await chain.treasury('ctrl');
  const tap = protocol.master(treasury.address);
  const numbers = protocol.collection(treasury.address,tap.address,false,toNano('10'));
  for (const c of [tap,numbers]) await treasury.send({to:c.address,init:c.init,value:toNano('1')});
  async function makeWallet() {
    const keys = keyPairFromSeed(randomBytes(32));
    const wallet = chain.openContract(WalletContractV4.create({workchain:0,publicKey:keys.publicKey}));
    await treasury.send({to:wallet.address,value:toNano('2'),bounce:false});
    const send = async (to,body) => wallet.sendTransfer({seqno:await wallet.getSeqno(),secretKey:keys.secretKey,
      sendMode:SendMode.PAY_GAS_SEPARATELY,messages:[internal({to,value:toNano('0.2'),body,bounce:false})]});
    await send(treasury.address);
    return {wallet,keys,send};
  }
  const alice = await makeWallet(), bob = await makeWallet();
  const addressOf = async (to,method,args) => (await chain.runGetMethod(to,method,args)).stackReader.readAddress();
  await treasury.send({to:tap.address,value:toNano('0.3'),body:protocol.mint(alice.wallet.address,toNano('10'))});
  const aw = await addressOf(tap.address,'get_wallet_address',[protocol.argAddress(alice.wallet.address)]);
  await alice.send(aw,protocol.deposit(numbers.address,alice.wallet.address,toNano('10')));
  const number = '+33312345678';
  await alice.send(numbers.address,protocol.buy(number));
  const adapter = new TonChain('https://example.invalid', numbers.address.toRawString());
  async function account(_, address) {
    const state = (await chain.getContract(address)).accountState;
    if (state?.type !== 'active') return {account:{state:{type:'uninit'}}};
    return {account:{state:{type:'active',code:state.state.code.toBoc().toString('base64'),data:state.state.data.toBoc().toString('base64')}}};
  }
  adapter.rpc = {getLastBlock:async()=>({last:{seqno:1}}), getAccount:account, getAccountLite:account,
    async runMethod(_,address,method,args) {return {exitCode:0,reader:(await chain.runGetMethod(address,method,args)).stackReader};}};
  const domain = 'ctrl.example.org';
  const auth = new Auth({file:':memory:',network:'-3',domain,chain:adapter,secret:randomBytes(32)});
  t.after(()=>auth.db.close());
  function login({wallet,keys}) {
    const {payload} = auth.challenge(number), timestamp = auth.now();
    const signature = sign(proofDigest(wallet.address,domain,timestamp,payload),keys.secretKey);
    return auth.login({number,network:'-3',address:wallet.address.toRawString(),
      proof:{domain:{value:domain,lengthBytes:domain.length},timestamp,payload,signature:signature.toString('base64')}});
  }
  const first = await login(alice), before = await auth.me(first.token);
  const item = await addressOf(numbers.address,'get_nft_address_by_index',[{type:'int',value:protocol.numberIndex(number)}]);
  await alice.send(item,protocol.transferItem(bob.wallet.address,alice.wallet.address));
  await assert.rejects(auth.me(first.token),{status:401});
  await assert.rejects(login(alice),{status:403});
  const buyer = await auth.me((await login(bob)).token);
  assert.equal(buyer.subject,before.subject);
  assert.notEqual(buyer.privateSubject,before.privateSubject);
  assert.equal(buyer.wallet,bob.wallet.address.toRawString());
});

