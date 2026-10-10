import {readFileSync} from 'node:fs';
import {X509Certificate, createPrivateKey, createPublicKey} from 'node:crypto';
import {createSecureContext} from 'node:tls';

try {
  const [domain, certPath, keyPath] = process.argv.slice(2);
  const cert = readFileSync(certPath), key = readFileSync(keyPath);
  const leaf = new X509Certificate(cert);
  if (!leaf.checkHost(domain) || Date.parse(leaf.validFrom) > Date.now() || Date.parse(leaf.validTo) <= Date.now() + 60000)
    throw Error('Certificate hostname or validity does not match');
  const publicKey = value => value.export({type:'spki',format:'der'});
  if (!publicKey(leaf.publicKey).equals(publicKey(createPublicKey(createPrivateKey(key))))) throw Error('Certificate key does not match');
  createSecureContext({cert,key});
  console.log(JSON.stringify({validUntil:leaf.validTo}));
} catch {
  console.error('HTTPS 인증서의 도메인, 만료일, PEM 형식 또는 개인 키를 확인하세요.');
  process.exitCode = 1;
}
