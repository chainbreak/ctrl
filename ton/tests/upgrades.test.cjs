const test=require('node:test'),assert=require('node:assert/strict');
const {keyPairFromSeed}=require('@ton/crypto'),{Blockchain}=require('@ton/sandbox'),{toNano}=require('@ton/core');
const {randomBytes}=require('node:crypto'),p=require('../wrappers/protocol.cjs');
test('signed gift edition is wallet-bound and replay keeps the owner after transfer',async()=>{
  const chain=await Blockchain.create(),admin=await chain.treasury('admin'),alice=await chain.treasury('alice'),bob=await chain.treasury('bob');
  const keys=keyPairFromSeed(randomBytes(32)),shop=p.upgrades(admin.address,keys.publicKey),index=59n;
  await admin.send({to:shop.address,value:toNano('1'),init:shop.init});
  const get=async(a,method,args=[])=>(await chain.runGetMethod(a,method,args)).stackReader;
  const item=(await get(shop.address,'get_nft_address_by_index',[{type:'int',value:index}])).readAddress();
  const claim=p.voucher(shop.address,index,alice.address,'Облако',keys.secretKey);
  await bob.send({to:shop.address,value:toNano('.2'),body:p.voucher(shop.address,index,bob.address,'Облако',keyPairFromSeed(randomBytes(32)).secretKey)});
  assert.notEqual((await chain.getContract(item)).accountState?.type,'active');
  await bob.send({to:shop.address,value:toNano('.2'),body:claim});
  const owner=async()=>{const r=await get(item,'get_nft_data');assert.ok(r.readBoolean());r.readBigNumber();r.readAddress();return r.readAddress();};
  assert.ok((await owner()).equals(alice.address));await alice.send({to:item,value:toNano('.2'),body:p.transferItem(bob.address,alice.address)});
  await alice.send({to:shop.address,value:toNano('.2'),body:claim});assert.ok((await owner()).equals(bob.address));
});

