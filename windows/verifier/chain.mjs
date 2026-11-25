import { Cell, Address } from '@ton/core';
import { TonClient4, WalletContractV4, WalletContractV5R1 } from '@ton/ton';
import { sha256 } from './proof.mjs';

const template = {workchain: 0, publicKey: Buffer.alloc(32)};
const v4 = WalletContractV4.create(template).init.code.hash();
const v5 = WalletContractV5R1.create(template).init.code.hash();

// Derive the key from authenticated contract state, never from browser input.
export function walletKey(state) {
  if (state.type !== 'active' || !state.code || !state.data) return null;
  const code = Cell.fromBase64(state.code).hash();
  if (!code.equals(v4) && !code.equals(v5)) return null;
  const data = Cell.fromBase64(state.data).beginParse();
  if (code.equals(v5) && !data.loadBit()) return null;
  data.skip(64);
  return data.loadBuffer(32);
}
export class TonChain {
  constructor(endpoint, collection) {
    this.rpc = new TonClient4({endpoint, timeout: 7000});
    this.collection = Address.parse(collection);
    this.ownerCache = new Map();
    this.cacheBlock = -1;
  }
  async snapshot() { return (await this.rpc.getLastBlock()).last.seqno; }
  async key(block, wallet) { return walletKey((await this.rpc.getAccount(block, wallet)).account.state); }
  async get(block, address, method, args = []) {
    const call = await this.rpc.runMethod(block, address, method, args);
    if (call.exitCode !== 0 && call.exitCode !== 1) throw Error('TON getter failed: ' + method);
    return call.reader;
  }
  async owner(block, number) {
    // Share immutable results within a block. Each permission check still asks
    // the RPC for its latest block before looking up an owner.
    if (block < this.cacheBlock) return this.readOwner(block, number);
    if (block > this.cacheBlock) { this.cacheBlock = block; this.ownerCache.clear(); }
    const cached = this.ownerCache.get(number);
    if (cached) return cached;
    if (this.ownerCache.size >= 256) this.ownerCache.delete(this.ownerCache.keys().next().value);
    const pending = this.readOwner(block, number);
    this.ownerCache.set(number, pending);
    try { return await pending; }
    catch (error) {
      if (this.ownerCache.get(number) === pending) this.ownerCache.delete(number);
      throw error;
    }
  }
  async readOwner(block, number) {
    const index = BigInt('0x' + sha256(number).toString('hex'));
    const item = (await this.get(block, this.collection, 'get_nft_address_by_index', [{type:'int', value:index}])).readAddress();
    if ((await this.rpc.getAccountLite(block, item)).account.state.type !== 'active') return null;
    const data = await this.get(block, item, 'get_nft_data');
    if (!data.readBoolean() || data.readBigNumber() !== index || !data.readAddress().equals(this.collection)) return null;
    const owner = data.readAddressOpt();
    if (!owner) return null;
    const version = (await this.get(block, item, 'get_ownership_version')).readBigNumber().toString();
    return {item:item.toRawString(), wallet:owner.toRawString(), version};
  }
}

