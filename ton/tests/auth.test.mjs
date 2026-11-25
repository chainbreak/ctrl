import test from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes, createHash } from 'node:crypto';
import { once } from 'node:events';
import { Address, beginCell } from '@ton/core';
import { WalletContractV4, WalletContractV5R1 } from '@ton/ton';
import { keyPairFromSeed, sign } from '@ton/crypto';
import { Auth } from '../backend/auth.mjs';
import { TonChain, walletKey } from '../backend/chain.mjs';
import { proofDigest } from '../backend/proof.mjs';
import { createApi } from '../backend/server.mjs';

const hash = bytes => createHash('sha256').update(bytes).digest();
const number = '+33300000001', domain = 'auth.example.org';
const raw = () => new Address(0, randomBytes(32));
function signer() {
  const pair = keyPairFromSeed(randomBytes(32));
  return {pair, address: WalletContractV4.create({workchain:0,publicKey:pair.publicKey}).address};
}
// Independent wire encoder following TON Connect's byte layout.
function signed(wallet, payload, now, host = domain) {
  const address = wallet.address;
  const header = Buffer.alloc(40);
  header.writeInt32BE(address.workChain); address.hash.copy(header,4); header.writeUInt32LE(Buffer.byteLength(host),36);
  const timestamp = Buffer.alloc(8); timestamp.writeBigUInt64LE(BigInt(now));
  const inner = hash(Buffer.concat([Buffer.from('ton-proof-item-v2/'),header,Buffer.from(host),timestamp,Buffer.from(payload)]));
  const digest = hash(Buffer.concat([Buffer.from('ffff746f6e2d636f6e6e656374','hex'),inner]));
  assert.ok(digest.equals(proofDigest(address,host,now,payload)));
  return {number,network:'-3',address:address.toRawString(),proof:{timestamp:now,payload,domain:{lengthBytes:Buffer.byteLength(host),value:host},
    signature:sign(digest,wallet.pair.secretKey).toString('base64')}};
}
function fixture(t) {
  const alice = signer(), bob = signer(), item = raw().toRawString();
  const chain = {collection:raw(), holder:alice.address.toRawString(), version:'0', offline:false,
    async snapshot(){ if (this.offline) throw Error('offline'); return 100; },
    async key(_,address){ return [alice,bob].find(p => p.address.equals(address))?.pair.publicKey; },
    async owner(_,label){ return label === number ? {wallet:this.holder,item,version:this.version} : null; }};
  let time = 1700000000;
  const auth = new Auth({file:':memory:',chain,network:'-3',domain,secret:randomBytes(32),now:()=>time});
  t.after(()=>auth.db.close());
  const request = (wallet=alice) => signed(wallet,auth.challenge(number).payload,time);
  return {auth,chain,alice,bob,request,advance: seconds => { time += seconds; }};
}
test('wallet state yields only supported active keys (V4R2 / V5R1)', () => {
  const {pair} = signer();
  for (const type of [WalletContractV4,WalletContractV5R1]) {
    const {init} = type.create({workchain:0,publicKey:pair.publicKey});
    const state = {type:'active',code:init.code.toBoc().toString('base64'),data:init.data.toBoc().toString('base64')};
    assert.ok(walletKey(state).equals(pair.publicKey));
    if (type === WalletContractV5R1) {
      const s = init.data.beginParse(); s.loadBit();
      assert.equal(walletKey({...state,data:beginCell().storeBit(false).storeSlice(s).endCell().toBoc().toString('base64')}),null);
    }
    assert.equal(walletKey({...state,code:beginCell().endCell().toBoc().toString('base64')}),null);
  }
  assert.equal(walletKey({type:'uninit'}),null);
});
test('RPC ownership reader pins collection, item, index and version to one block', async () => {
  const collection = raw(), item = raw(), wallet = raw();
  const index = BigInt('0x'+hash(number).toString('hex'));
  const adapter = new TonChain('https://example.invalid',collection.toRawString());
  let returnedCollection = collection;
  adapter.rpc = {getAccountLite: async (height, address) => {
    assert.equal(height,15); assert.ok(address.equals(item)); return {account:{state:{type:'active'}}};
  },runMethod:async (height,address,method,args) => {
    assert.equal(height,15);
    let reader;
    if (method === 'get_nft_address_by_index') {
      assert.ok(address.equals(collection)); assert.equal(args[0].value,index); reader = {readAddress:()=>item};
    } else {
      assert.ok(address.equals(item));
      reader = method === 'get_nft_data' ? {readBoolean:()=>true,readBigNumber:()=>index,readAddress:()=>returnedCollection,readAddressOpt:()=>wallet}
        : {readBigNumber:()=>4n};
    }
    return {exitCode:0,reader};
  }};
  assert.deepEqual(await adapter.owner(15,number),{wallet:wallet.toRawString(),item:item.toRawString(),version:'4'});
  returnedCollection = raw(); assert.equal(await adapter.owner(15,number),null);
});
test('domain, chain, timestamp, signature, number and owner are bound to the challenge', async t => {
  const {auth,request,alice,bob} = fixture(t);
  assert.throws(()=>auth.challenge('+33312345678\n'));
  for (const edit of [r=>r.network='-239',r=>r.proof.timestamp-=301,r=>r.proof.domain.value='bad.example',
    r=>r.proof.domain.lengthBytes++,r=>r.proof.signature=Buffer.alloc(64).toString('base64'),r=>r.number='+99900000001',
    r=>{r.address=bob.address.toRawString();r.publicKey=alice.pair.publicKey.toString('hex');}]) {
    const r = request(); edit(r); await assert.rejects(auth.login(r),{status:401});
  }
  await assert.rejects(auth.login(request(bob)),{status:403});
  const original = request(), both = await Promise.allSettled([auth.login(original),auth.login(original)]);
  assert.equal(both.filter(x=>x.status==='fulfilled').length,1);
});
test('resale transfers the public profile, separates inboxes and revokes prior credentials', async t => {
  const {auth,chain,bob,request} = fixture(t);
  const first = await auth.login(request());
  const me = await auth.me(first.token);
  chain.offline = true; await assert.rejects(auth.me(first.token),{status:503});
  chain.offline = false; assert.equal((await auth.me(first.token)).subject,me.subject);
  chain.holder = bob.address.toRawString(); chain.version = '1'; await auth.sweep();
  await assert.rejects(auth.me(first.token),{status:401});
  await assert.rejects(auth.login(request()),{status:403});
  const second = await auth.login(request(bob));
  const buyer = await auth.me(second.token);
  assert.equal(buyer.subject,me.subject);
  assert.notEqual(buyer.privateSubject,me.privateSubject);
  await auth.logout(second.token); await assert.rejects(auth.me(second.token),{status:401});
});
test('sale and return between polls, expiry and fresh challenge',async t=>{
  const {auth,chain,request,advance} = fixture(t);
  const previous = await auth.login(request());
  chain.version='2';
  await assert.rejects(auth.me(previous.token),{status:401});
  const expiredRequest = request();
  advance(301); await assert.rejects(auth.login(expiredRequest),{status:401});
  const current = await auth.login(request());
  advance(1801); await assert.rejects(auth.me(current.token),{status:401});
});
test('HTTP challenge → proof → protected route → logout and rejection', async t => {
  const {auth,alice} = fixture(t);
  const server = createApi(auth,'https://'+domain);
  server.listen(0,'127.0.0.1'); await once(server,'listening');
  t.after(()=>new Promise(resolve=>server.close(resolve)));
  const origin = 'http://127.0.0.1:'+server.address().port;
  const post = (path, obj, headers={}) => fetch(origin+path,{method:'POST',headers:{'Content-Type':'application/json',...headers},body:JSON.stringify(obj)});
  for (const path of ['/','/login.js','/tonconnect.js','/icon.png','/tonconnect-manifest.json']) {
    const page = await fetch(origin+path); assert.equal(page.status,200); await page.arrayBuffer();
  }
  const {payload} = await (await post('/auth/challenge',{number})).json();
  const session = await post('/auth/verify',signed(alice,payload,auth.now()));
  assert.equal(session.status,200);
  const headers = {Authorization:'Bearer '+(await session.json()).token};
  assert.equal((await (await fetch(origin+'/auth/me',{headers})).json()).number,number);
  assert.equal((await post('/auth/logout',{},headers)).status,200);
  assert.equal((await fetch(origin+'/auth/me',{headers})).status,401);
  assert.equal((await post('/auth/challenge',{number},{Origin:'https://foreign.example'})).status,403);
});

