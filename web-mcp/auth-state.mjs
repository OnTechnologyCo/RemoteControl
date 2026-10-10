// 비밀번호를 저장하지 않는다. 설치 폴더의 ACL로 보호된 proxyKey에 인증 상태를 봉인한다.
import { createHash, createCipheriv, createDecipheriv, randomBytes } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync, renameSync, unlinkSync } from 'node:fs';
import { join } from 'node:path';

export class AuthState {
  constructor(directory, proxyKey, issuer) {
    this.file = join(directory, 'auth-state.bin');
    this.key = createHash('sha256').update('RemoteControl OAuth state v1\n' + proxyKey).digest();
    this.issuer = issuer;
    this.aad = Buffer.from('RemoteControl OAuth state v1');
  }
  read() {
    if (!existsSync(this.file)) return null;
    const data = readFileSync(this.file);
    if (data.length < 34 || data.length > 2 * 1024 * 1024 || data.subarray(0, 6).toString() !== 'RCMCP1') throw Error('Invalid encrypted OAuth state');
    const cipher = createDecipheriv('aes-256-gcm', this.key, data.subarray(6, 18));
    cipher.setAAD(this.aad); cipher.setAuthTag(data.subarray(18, 34));
    const clear = Buffer.concat([cipher.update(data.subarray(34)), cipher.final()]);
    try { const value = JSON.parse(clear.toString('utf8')); return value.issuer === this.issuer ? value : null; } finally { clear.fill(0); }
  }
  write(value) {
    const clear = Buffer.from(JSON.stringify({...value,issuer:this.issuer})), iv = randomBytes(12);
    let sealed;
    try {
      const cipher = createCipheriv('aes-256-gcm', this.key, iv); cipher.setAAD(this.aad);
      const body = Buffer.concat([cipher.update(clear), cipher.final()]);
      sealed = Buffer.concat([Buffer.from('RCMCP1'), iv, cipher.getAuthTag(), body]);
    } finally { clear.fill(0); }
    const temp = this.file + '.' + randomBytes(8).toString('hex') + '.tmp';
    try { writeFileSync(temp, sealed, {mode:0o600, flag:'wx'}); renameSync(temp, this.file); }
    finally { if (existsSync(temp)) unlinkSync(temp); }
  }
}
