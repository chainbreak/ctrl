const test=require('node:test');
const assert=require('node:assert/strict');
const {Blockchain}=require('@ton/sandbox');
const {toNano}=require('@ton/core');
const p=require('../wrappers/protocol.cjs');

test('username auction escrows TAP, refunds outbid owner and issues one transferable NFT',async()=>{
  const chain=await Blockchain.create();chain.now=1000000;
  const staff=await chain.treasury('staff'),a=await chain.treasury('a'),b=await chain.treasury('b');
  const tap=p.master(staff.address),shop=p.usernames(staff.address,tap.address,toNano('1'));
  for(const c of [tap,shop])await staff.send({to:c.address,value:toNano('1'),init:c.init});
  const get=async(address,name,args=[])=>(await chain.runGetMethod(address,name,args)).stackReader;
  const send=(who,to,body)=>who.send({to,body,value:toNano('0.3')});
  const credits=async who=>(await get(shop.address,'get_credit',[p.argAddress(who.address)])).readBigNumber();
  for(const who of [a,b]){await send(staff,tap.address,p.mint(who.address,toNano('6')));
    const w=(await get(tap.address,'get_wallet_address',[p.argAddress(who.address)])).readAddress();
    await send(who,w,p.deposit(shop.address,who.address,toNano('6')));}
  await send(a,shop.address,p.bid('@root',toNano('2')));assert.equal(await credits(a),toNano('6'));
  await send(a,shop.address,p.bid('@key',toNano('2')));assert.equal(await credits(a),toNano('4'));
  await send(b,shop.address,p.bid('@key',toNano('2')));assert.equal(await credits(b),toNano('6'));
  await send(b,shop.address,p.bid('@key',toNano('3')));assert.equal(await credits(b),toNano('3'));assert.equal(await credits(a),toNano('6'));
  await send(a,shop.address,p.settle('@key'));assert.equal(await credits(staff),0n);
  chain.now+=86401;await send(a,shop.address,p.settle('@key'));assert.equal(await credits(staff),toNano('3'));
  const arg=[{type:'int',value:p.usernameIndex('@key')}];assert.equal((await get(shop.address,'get_auction',arg)).readBigNumber(),-1n);
  const item=(await get(shop.address,'get_nft_address_by_index',arg)).readAddress(),info=await get(item,'get_nft_data');
  assert.ok(info.readBoolean());assert.equal(info.readBigNumber(),p.usernameIndex('@key'));assert.ok(info.readAddress().equals(shop.address));assert.ok(info.readAddress().equals(b.address));
  await send(a,shop.address,p.settle('@key'));assert.equal(await credits(staff),toNano('3'));
});

