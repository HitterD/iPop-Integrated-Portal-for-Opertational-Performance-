const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

// Egress v1.15.0 pkg/config/service.go reads LIVEKIT_API_KEY / LIVEKIT_API_SECRET.
// Wrong env names do not fail health checks: StartEgress fails only when someone records.
test('Egress uses supported credential variables backed by the LiveKit secret', () => {
  const manifest = fs.readFileSync(path.join(__dirname, '../deploy/k8s/apps/ipop/media.yaml'), 'utf8').replace(/\r\n/g, '\n');
  const deployment = manifest.slice(manifest.indexOf('name: egress\n'), manifest.indexOf('# ONLYOFFICE Document Server'));
  assert.match(deployment, /image: livekit\/egress:v1\.15\.0/);
  for (const [variable, key] of [['LIVEKIT_API_KEY', 'api-key'], ['LIVEKIT_API_SECRET', 'api-secret']]) {
    const expression = new RegExp(`- name: ${variable}\\r?\\n\\s+valueFrom: \\{ secretKeyRef: \\{ name: livekit-keys, key: ${key} \\} \\}`);
    assert.match(deployment, expression, `${variable} must use the existing LiveKit credential, not a hardcoded secret`);
  }
  assert.doesNotMatch(deployment, /- name: EGRESS_API_(KEY|SECRET)/);
});
