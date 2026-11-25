import { NetworkProvider } from '@ton/blueprint';
import { toNano } from '@ton/core';
const protocol = require('../wrappers/protocol.cjs');

export async function run(provider: NetworkProvider) {
  const signer = provider.sender();
  if (!signer.address) throw Error('Select your signing wallet');
  const tap = protocol.master(signer.address);
  const numbers = protocol.collection(signer.address, tap.address, false, toNano('10'));
  const gifts = protocol.collection(signer.address, tap.address, true, toNano('3'));
  const usernames = protocol.usernames(signer.address,tap.address,toNano('1'));
  const publicKey=process.env.CTRL_UPGRADE_PUBLIC_KEY;
  const editions=publicKey && /^[a-fA-F0-9]{64}$/.test(publicKey)?{upgrades:protocol.upgrades(signer.address,Buffer.from(publicKey,'hex'))}:{};
  for (const [name, contract] of Object.entries({tap,numbers,gifts,usernames,...editions}) as [string,typeof tap][]) {
    if (!(await provider.isContractDeployed(contract.address))) {
      await signer.send({to: contract.address, value: toNano('0.2'), init: contract.init});
      await provider.waitForDeploy(contract.address);
    }
    provider.ui().write(name + ' = ' + contract.address.toRawString());
  }
}

