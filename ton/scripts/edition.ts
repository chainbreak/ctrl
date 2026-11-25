import { NetworkProvider } from '@ton/blueprint';
import { Address, toNano } from '@ton/core';
const protocol = require('../wrappers/protocol.cjs');

// Deploy a separate fixed-price edition for a gift, badge or sticker.
export async function run(provider: NetworkProvider, args: string[]) {
  const [master, price, name, image] = args;
  if (!master || !price || !name || !image) throw Error('Expected TAP master, price, edition name and image URI');
  const signer = provider.sender();
  if (!signer.address) throw Error('Signing wallet required');
  const collection = protocol.collection(signer.address, Address.parse(master), true, toNano(price), {
    name: 'Ctrl · ' + name, item: {name, image}
  });
  if (!(await provider.isContractDeployed(collection.address))) {
    await signer.send({to: collection.address, init: collection.init, value: toNano('0.2')});
    await provider.waitForDeploy(collection.address);
  }
  provider.ui().write(collection.address.toRawString());
}

