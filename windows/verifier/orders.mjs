import {Address,Cell,fromNano,toNano} from '@ton/core';
import {canonicalNumber,check,parseWallet} from './proof.mjs';
import protocol from './protocol.cjs';

export async function prepareOrder(chain,config,data) {
    check(config.network===data.network,400,'Выберите ту же сеть в кошельке.');
    check(['number','gift','username'].includes(data.kind) && ['quote','deposit','buy','withdraw','bid','settle'].includes(data.operation),400,'Неизвестная операция.');
    const gifts=data.kind==='gift', names=data.kind==='username';
    check(names?data.operation!=='buy':!['bid','settle'].includes(data.operation),400,'Выберите операцию для этого актива.');
    check(!names || config.usernameCollection,503,'Коллекция юзернеймов ещё не настроена.');
    check(!gifts || config.giftCollection,503,'Коллекция подарков ещё не настроена.');
    const owner=parseWallet(data.wallet), shop=names?Address.parse(config.usernameCollection):gifts?Address.parse(config.giftCollection):chain.collection;
    const seq=await chain.snapshot();
    async function settings(address) {
        const {account}=await chain.rpc.getAccount(seq,address);
        check(account.state.type==='active',503,'Коллекция ещё не активна в выбранной сети.');
        const s=Cell.fromBase64(account.state.data).beginParse();
        return {admin:s.loadAddress(),master:s.loadAddress(),gifts:s.loadBit(),price:s.loadCoins()};
    }
    const contract=await settings(shop);
    check(contract.gifts===gifts && contract.price>0n,503,'Проверьте адрес коллекции.');
    if(gifts||names)check((await settings(chain.collection)).master.equals(contract.master),503,'Коллекции используют разные токены.');
    const credit=(await chain.get(seq,shop,'get_credit',[protocol.argAddress(owner)])).readBigNumber();
    const wallet=(await chain.get(seq,contract.master,'get_wallet_address',[protocol.argAddress(owner)])).readAddress();
    const active=(await chain.rpc.getAccountLite(seq,wallet)).account.state.type==='active';
    const balance=active?(await chain.get(seq,wallet,'get_wallet_data')).readBigNumber():0n;
    const result={summary:'Цена '+fromNano(contract.price)+' TAP · В кошельке '+fromNano(balance)+' TAP · Внесено '+fromNano(credit)+' TAP'};
    let desired=contract.price,handle;
    if(names){
        handle=String(data.handle||'').trim().toLowerCase();
        check(/^@[a-z][a-z0-9_]{2,3}$/.test(handle),400,'Введите редкий юзернейм из 3–4 символов с @.');
        const current=await chain.get(seq,shop,'get_auction',[{type:'int',value:protocol.usernameIndex(handle)}]);
        const end=current.readNumber(),high=current.readBigNumber();
        result.summary+=(end===-1?' · Продан':end?' · Лучшая ставка '+fromNano(high)+' TAP · До '+new Date(end*1000).toISOString():' · Торги откроются с первой ставкой');
        if(['bid','deposit'].includes(data.operation)){check(/^\d{1,12}(\.\d{1,9})?$/.test(String(data.amount||'')),400,'Введите ставку в TAP.');desired=toNano(data.amount);check(end>=0 && (!end||end>Date.now()/1000) && desired>=contract.price && desired>high,409,'Ставка слишком мала или торги закрыты.');}
        if(data.operation==='settle')check(end>0&&end<=Date.now()/1000,409,'Торги ещё не завершены.');
    }
    if(data.operation==='quote')return result;
    let destination=shop, body, gas;
    if(data.operation==='deposit'){
        const amount=desired-credit;
        check(amount>0n,409,'Для покупки уже достаточно TAP.');
        check(balance>=amount,409,'В кошельке недостаточно TAP.');
        destination=wallet;body=protocol.deposit(shop,owner,amount);gas='0.2';
    }else if(data.operation==='withdraw'){
        check(credit>0n,409,'Нет остатка для вывода.');
        body=protocol.withdraw(credit);gas='0.17';
    }else if(names){
        if(data.operation==='bid')check(credit>=desired,409,'Сначала внесите TAP.');
        body=data.operation==='settle'?protocol.settle(handle):protocol.bid(handle,desired);gas='0.16';
    }else{
        check(credit>=contract.price,409,'Сначала внесите TAP.');
        if(!gifts){canonicalNumber(data.number);check(!data.number.startsWith('+999')||contract.admin.equals(owner),403,'Выпуск +999 доступен администрации.');
            check(!(await chain.owner(seq,data.number)),409,'Этот номер уже выпущен.');}
        body=protocol.buy(gifts?undefined:data.number);gas='0.14';
    }
    result.transaction={validUntil:Math.floor(Date.now()/1000)+240,network:config.network,from:owner.toRawString(),
        messages:[{address:destination.toString({bounceable:true,testOnly:config.network==='-3'}),amount:toNano(gas).toString(),payload:body.toBoc().toString('base64')}]};
    return result;
}

