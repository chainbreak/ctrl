require('ts-node/register');
const { compile } = require('@ton/blueprint');
const fs = require('node:fs/promises');
(async () => {
  await fs.mkdir('build', { recursive: true });
  for (const name of ['TapMaster', 'TapWallet', 'Item', 'Collection', 'Usernames', 'Upgrades']) {
    const code = await compile(name);
    await fs.writeFile('build/' + name + '.compiled.json', JSON.stringify({
      hash: code.hash().toString('hex'), hex: code.toBoc().toString('hex'),
    }, null, 2));
    console.log(name, code.hash().toString('hex'));
  }
})().catch(error => { console.error(error); process.exitCode = 1; });

