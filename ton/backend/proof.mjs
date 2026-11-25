import { createHash } from 'node:crypto';
import { signVerify } from '@ton/crypto';
import { Address } from '@ton/core';

export const sha256 = input => createHash('sha256').update(input).digest();
export class HttpError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}
export function check(condition, status = 401, message = 'Не удалось подтвердить вход') {
  if (!condition) throw new HttpError(status, message);
}
export function canonicalNumber(number) {
  check(typeof number === 'string' && number.length === 12 && /^\+(333|999)[0-9]{8}$/.test(number), 400, 'Проверьте формат номера');
  return number;
}
export function parseWallet(text) {
  try { const a = Address.parse(text); if (a.workChain === 0) return a; } catch {}
  throw new HttpError(400, 'Некорректный адрес кошелька');
}
export function proofDigest(address, domain, timestamp, payload) {
  const bytes = Buffer.from(domain);
  const wc = Buffer.alloc(4), len = Buffer.alloc(4), time = Buffer.alloc(8);
  wc.writeInt32BE(address.workChain); len.writeUInt32LE(bytes.length); time.writeBigUInt64LE(BigInt(timestamp));
  const message = Buffer.concat([Buffer.from('ton-proof-item-v2/'), wc, address.hash, len, bytes, time, Buffer.from(payload)]);
  return sha256(Buffer.concat([Buffer.from([255,255]), Buffer.from('ton-connect'), sha256(message)]));
}
export function checkProof(proof, domain, now) {
  check(proof && proof.domain?.value === domain && proof.domain.lengthBytes === Buffer.byteLength(domain));
  check(Number.isSafeInteger(proof.timestamp) && proof.timestamp > now - 300 && proof.timestamp <= now + 15);
  check(typeof proof.payload === 'string' && /^[a-f0-9]{64}$/.test(proof.payload) && proof.payload.length === 64);
  check(typeof proof.signature === 'string' && proof.signature.length === 88);
}
export function verifySignature(address, proof, key) {
  const signature = Buffer.from(proof.signature, 'base64');
  check(key && signature.length === 64 && signature.toString('base64') === proof.signature &&
    signVerify(proofDigest(address, proof.domain.value, proof.timestamp, proof.payload), signature, key));
}

