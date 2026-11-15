const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

// Without these tolerations a pod stays bound to a dead node for the default 300 s.
// Measured 2026-11-12 with 1028 users: API/Web/DB moved in ~1 min, Redis took ~5.5 min
// and SignalR realtime stayed down that whole time.
const read = (file) => fs.readFileSync(path.join(__dirname, '..', file), 'utf8').replace(/\r\n/g, '\n');
const docs = (file) => read(file).split(/^---$/m);
const deployment = (file, name) => docs(file).find((d) => /kind: Deployment/.test(d) && new RegExp(`^  name: ${name}$`, 'm').test(d));

for (const [file, name] of [
  ['deploy/k8s/apps/ipop-base/redis.yaml', 'redis'],
  ['deploy/k8s/apps/ipop/media.yaml', 'livekit-redis'],
  ['deploy/k8s/apps/ipop/media.yaml', 'egress'],
  ['deploy/k8s/apps/ipop/media.yaml', 'livekit'],
  ['deploy/k8s/apps/ipop/media.yaml', 'onlyoffice'],
  ['deploy/k8s/apps/ipop-base/api.yaml', 'ipop-api'],
  ['deploy/k8s/apps/ipop-base/web.yaml', 'ipop-web'],
]) {
  test(`${name} leaves a dead node after 10 s`, () => {
    const d = deployment(file, name);
    assert.ok(d, `${name} deployment not found in ${file}`);
    for (const taint of ['unreachable', 'not-ready']) {
      assert.match(d, new RegExp(`key: node\.kubernetes\.io/${taint}, operator: Exists, effect: NoExecute, tolerationSeconds: 10`), `${name} missing ${taint} toleration`);
    }
  });
}

// Measured 2026-11-12: with ipop-02 down, LiveKit (media nodes only) stayed Pending for the whole
// outage because Egress held 1 CPU on the other media node. Egress must be preemptible so calls
// come back first; it may not preempt anything itself.
test('LiveKit can evict Egress when only one media node is left', () => {
  const egress = deployment('deploy/k8s/apps/ipop/media.yaml', 'egress');
  assert.match(egress, /priorityClassName: ipop-preemptible/);
  const pc = docs('deploy/k8s/apps/ipop/media.yaml').find((d) => /kind: PriorityClass/.test(d) && /name: ipop-preemptible/.test(d));
  assert.ok(pc, 'PriorityClass ipop-preemptible missing');
  assert.match(pc, /value: -\d+/);
  assert.match(pc, /preemptionPolicy: Never/);
  assert.match(pc, /globalDefault: false/);
});
