import { DatabaseSync } from 'node:sqlite';
import { randomBytes, randomUUID } from 'node:crypto';
import { SignJWT, jwtVerify } from 'jose';
import { canonicalNumber, check, checkProof, HttpError, parseWallet, sha256, verifySignature } from './proof.mjs';

export class Auth {
  constructor({file, chain, network, domain, secret, now = () => Math.floor(Date.now()/1000)}) {
    if (secret.length < 32) throw Error('JWT key must contain at least 32 bytes');
    Object.assign(this, {chain, network, domain, secret, now});
    this.db = new DatabaseSync(file);
    this.db.exec(`PRAGMA journal_mode=WAL;
      PRAGMA busy_timeout=5000;
      CREATE TABLE IF NOT EXISTS challenges(hash TEXT PRIMARY KEY, number TEXT NOT NULL, expires INTEGER NOT NULL);
      CREATE TABLE IF NOT EXISTS sessions(id TEXT PRIMARY KEY, subject TEXT NOT NULL, number TEXT NOT NULL,
        wallet TEXT NOT NULL, item TEXT NOT NULL, version TEXT NOT NULL, expires INTEGER NOT NULL);
      CREATE INDEX IF NOT EXISTS sessions_owner ON sessions(number,wallet);
    `);
    this.issuer = 'ctrl:' + network + ':' + domain + ':' + chain.collection.toRawString();
  }
  challenge(number) {
    canonicalNumber(number);
    const payload = randomBytes(32).toString('hex');
    const expires = this.now() + 300;
    this.db.prepare('INSERT INTO challenges VALUES (?,?,?)').run(sha256(payload).toString('hex'), number, expires);
    return {payload, expires};
  }
  async rpc(task) {
    try { return await task(); }
    catch { throw new HttpError(503, 'Нет ответа от TON. Попробуйте ещё раз'); }
  }
  async login({number, network, address, proof}) {
    canonicalNumber(number);
    check(network === this.network);
    const wallet = parseWallet(address);
    checkProof(proof, this.domain, this.now());
    const hash = sha256(proof.payload).toString('hex');
    const filter = ' WHERE hash=? AND number=? AND expires>?';
    check(this.db.prepare('SELECT 1 FROM challenges' + filter).get(hash, number, this.now()));
    const block = await this.rpc(() => this.chain.snapshot());
    const key = await this.rpc(() => this.chain.key(block, wallet));
    verifySignature(wallet, proof, key);
    const owner = await this.rpc(() => this.chain.owner(block, number));
    check(owner && owner.wallet === wallet.toRawString(), 403, 'Кошелёк не владеет этим номером');
    // The public number profile transfers with the NFT. Personal inboxes and staff
    // grants have a distinct identity for each wallet that holds that profile.
    const subject = sha256([this.issuer,owner.item].join('|')).toString('hex');
    const id = randomUUID(), expires = this.now() + 1800;
    const token = await new SignJWT({}).setProtectedHeader({alg:'HS256',typ:'JWT'}).setIssuer(this.issuer)
      .setAudience('ctrl-messenger').setSubject(subject).setJti(id).setIssuedAt(this.now()).setExpirationTime(expires).sign(this.secret);
    this.db.exec('BEGIN IMMEDIATE');
    try {
      check(this.db.prepare('DELETE FROM challenges' + filter).run(hash, number, this.now()).changes === 1);
      this.db.prepare('INSERT INTO sessions VALUES (?,?,?,?,?,?,?)').run(id, subject, number, owner.wallet, owner.item, owner.version, expires);
      this.db.exec('COMMIT');
    } catch (error) { this.db.exec('ROLLBACK'); throw error; }
    return {token, expires, number};
  }
  async session(token) {
    let payload;
    try {
      ({payload} = await jwtVerify(token, this.secret, {algorithms:['HS256'], issuer:this.issuer,
        audience:'ctrl-messenger', currentDate:new Date(this.now()*1000)}));
    } catch { throw new HttpError(401, 'Войдите снова'); }
    const row = this.db.prepare('SELECT * FROM sessions WHERE id=? AND subject=? AND expires>?').get(payload.jti, payload.sub, this.now());
    check(row, 401, 'Войдите снова');
    return row;
  }
  async owns(block, row) {
    const current = await this.rpc(() => this.chain.owner(block, row.number));
    if (current?.wallet === row.wallet && current.item === row.item && current.version === row.version) return true;
    this.db.prepare('DELETE FROM sessions WHERE number=? AND wallet=? AND item=? AND version=?').run(row.number, row.wallet, row.item, row.version);
    return false;
  }
  async me(token) {
    const row = await this.session(token);
    check(await this.owns(await this.rpc(() => this.chain.snapshot()), row), 401, 'Владелец номера изменился');
    const privateSubject = sha256([row.subject,row.wallet].join('|')).toString('hex');
    return {subject:row.subject, privateSubject, number:row.number, wallet:row.wallet, item:row.item};
  }
  async logout(token) { this.db.prepare('DELETE FROM sessions WHERE id=?').run((await this.session(token)).id); }
  async sweep() {
    this.db.prepare('DELETE FROM sessions WHERE expires<=?').run(this.now());
    this.db.prepare('DELETE FROM challenges WHERE expires<=?').run(this.now());
    const rows = this.db.prepare('SELECT DISTINCT number,wallet,item,version FROM sessions').all();
    if (!rows.length) return;
    const block = await this.rpc(() => this.chain.snapshot());
    for (const row of rows) await this.owns(block, row);
  }
}

