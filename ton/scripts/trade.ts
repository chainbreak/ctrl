import { NetworkProvider } from '@ton/blueprint';
import { Address, fromNano, toNano } from '@ton/core';
const p = require('../wrappers/protocol.cjs');

export async function run(provider: NetworkProvider, args: string[]) {
  // trade <mint|deposit|number|gift|withdraw|recover> <master> <collection> [argument]
  const [operation, tokenAddress, shopAddress, argument, recipient] = args;
  if (!tokenAddress || !shopAddress) throw Error('Expected operation, TAP master, collection and optional argument');
  const tap = Address.parse(tokenAddress), shop = Address.parse(shopAddress);
  const sender = provider.sender();
  if (!sender.address) throw Error('Signing wallet required');
  const raw = () => toNano(argument);
  const send = (to: Address, gas: string, body: any) => sender.send({to,value:toNano(gas),body});
  switch (operation) {
    case 'mint': return send(tap,'0.2',p.mint(recipient ? Address.parse(recipient) : sender.address,raw()));
    case 'deposit': {
      const result = await provider.provider(tap).get('get_wallet_address',[p.argAddress(sender.address)]);
      return send(result.stack.readAddress(),'0.2',p.deposit(shop,sender.address,raw()));
    }
    case 'number': return send(shop,'0.14',p.buy(argument));
    case 'gift': return send(shop,'0.14',p.buy());
    case 'transfer': {
      if (!recipient) throw Error('Expected number or gift index and recipient address');
      const index = argument.startsWith('+') ? p.numberIndex(argument) : BigInt(argument);
      const result = await provider.provider(shop).get('get_nft_address_by_index',[{type:'int',value:index}]);
      return send(result.stack.readAddress(),'0.1',p.transferItem(Address.parse(recipient),sender.address));
    }
    case 'withdraw': return send(shop,'0.17',p.withdraw(raw()));
    case 'recover': return send(shop,'0.14',p.recover(BigInt(argument)));
    case 'balance': {
      const result = await provider.provider(shop).get('get_credit',[p.argAddress(sender.address)]);
      provider.ui().write(fromNano(result.stack.readBigNumber()) + ' TAP');
      return;
    }
    default: throw Error('Unknown operation');
  }
}

