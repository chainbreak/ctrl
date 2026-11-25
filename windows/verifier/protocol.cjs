const { Address, Cell, beginCell, contractAddress, Dictionary, toNano } = require('@ton/core');
const { createHash } = require('node:crypto');
const { readFileSync } = require('node:fs');
const path = require('node:path');
const hash = value => BigInt('0x' + createHash('sha256').update(value, 'utf8').digest('hex'));

function artifact(name) {
  const json = JSON.parse(readFileSync(path.join(__dirname, '../build', name + '.compiled.json'), 'utf8'));
  return Cell.fromBoc(Buffer.from(json.hex, 'hex'))[0];
}
function metadata(fields) {
  const dict = Dictionary.empty(Dictionary.Keys.BigUint(256), Dictionary.Values.Cell());
  for (const [key, value] of Object.entries(fields)) {
    dict.set(hash(key), beginCell().storeUint(0, 8).storeStringTail(value).endCell());
  }
  return beginCell().storeUint(0, 8).storeDict(dict).endCell();
}
function contract(code, data) {
  const init = { code, data };
  return { address: contractAddress(0, init), init };
}
function master(admin) {
  const contents = metadata({name:'Ctrl TAP', symbol:'TAP', decimals:'9', description:'Token for the Ctrl marketplace'});
  return contract(artifact('TapMaster'), beginCell().storeCoins(0).storeAddress(admin)
    .storeRef(contents).storeRef(artifact('TapWallet')).endCell());
}
function collection(admin, tap, gifts, price, edition = {}) {
  if (price <= 0n) throw Error('Positive TAP price required');
  const codes = beginCell().storeRef(artifact('Item')).storeRef(artifact('TapWallet')).endCell();
  const gift = edition.item || {name:'Облачко Cloudflare',description:'Первый коллекционный подарок Ctrl'};
  const meta = beginCell().storeRef(metadata({name:edition.name || (gifts ? 'Ctrl Gifts' : 'Ctrl Numbers')}))
    .storeRef(metadata(gift)).endCell();
  const ledger = beginCell().storeDict(null).storeDict(null).storeDict(null).endCell();
  return contract(artifact('Collection'), beginCell().storeAddress(admin).storeAddress(tap)
    .storeBit(gifts).storeCoins(price).storeUint(0,64).storeUint(0,64)
    .storeRef(codes).storeRef(meta).storeRef(ledger).endCell());
}
function usernames(admin,tap,minimum) {
  const normal=collection(admin,tap,false,minimum,{name:'Ctrl Usernames'});
  const old=normal.init.data.beginParse();const meta=old.loadRef(); // first ref holds item and wallet code
  const fields=old.loadRef().beginParse();
  const info=beginCell().storeRef(fields.loadRef()).storeRef(fields.loadRef()).storeDict(null).endCell();
  return contract(artifact('Usernames'),beginCell().storeAddress(admin).storeAddress(tap).storeBit(false).storeCoins(minimum)
    .storeUint(0,64).storeUint(0,64).storeRef(meta).storeRef(info).storeRef(old.loadRef()).endCell());
}
function usernameIndex(handle){if(!/^@[a-z][a-z0-9_]{2,31}$/.test(handle))throw Error('Invalid username');return hash(handle);}
function bid(handle,amount){usernameIndex(handle);return header(0x43544101).storeCoins(amount).storeStringTail(handle).endCell();}
function settle(handle){usernameIndex(handle);return header(0x43544102).storeStringTail(handle).endCell();}
function upgrades(admin,publicKey) {
  const codes=beginCell().storeRef(artifact('Item')).endCell();
  return contract(artifact('Upgrades'),beginCell().storeAddress(admin).storeBuffer(publicKey).storeUint(0,64).storeRef(codes)
    .storeRef(metadata({name:'Ctrl Gifts · Editions'})).storeDict(null).endCell());
}
function voucher(collection,index,owner,name,secretKey) {
  const claim=beginCell().storeAddress(collection).storeUint(index,256).storeAddress(owner).storeRef(metadata({name})).endCell();
  const signature=require('@ton/crypto').sign(claim.hash(),secretKey);
  return header(0x43544701).storeBuffer(signature).storeRef(claim).endCell();
}
const header = (op, query = 0n) => beginCell().storeUint(op,32).storeUint(query,64);
const numberIndex = number => {
  if (!/^\+(333|999)[0-9]{8}$/.test(number) || number.length !== 12) throw Error('Invalid canonical number');
  return hash(number);
};
function mint(to, amount) {
  const internal = header(0x178d4519).storeCoins(amount).storeAddress(null).storeAddress(to)
    .storeCoins(0).storeBit(0).endCell();
  return header(21).storeAddress(to).storeCoins(toNano('0.12')).storeRef(internal).endCell();
}
function deposit(shop, owner, amount) {
  return header(0x0f8a7ea5).storeCoins(amount).storeAddress(shop).storeAddress(owner)
    .storeBit(0).storeCoins(toNano('0.06')).storeBit(0).endCell();
}
function buy(number) {
  const body = header(0x43545201);
  if (number !== undefined) { numberIndex(number); body.storeStringTail(number); }
  return body.endCell();
}
const withdraw = amount => header(0x43545203).storeCoins(amount).endCell();
const recover = index => header(0x43545202).storeUint(index,256).endCell();
const transferItem = (to, excess) => header(0x5fcc3d14).storeAddress(to).storeAddress(excess)
  .storeBit(0).storeCoins(0).storeBit(0).endCell();
const argAddress = address => ({type:'slice',cell:beginCell().storeAddress(address).endCell()});
module.exports = {voucher,upgrades,usernames,usernameIndex,bid,settle,artifact,master,collection,metadata,numberIndex,mint,deposit,buy,withdraw,recover,transferItem,argAddress};

