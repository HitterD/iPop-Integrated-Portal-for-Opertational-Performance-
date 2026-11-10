const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

// CallEndpoints.WithTokenAsync sends LiveKitUrl = liveKit.ServerUrl, which is the cluster-internal
// address (http://livekit.ipop.svc:7880). A browser cannot open that name: it is not in public DNS.
// The client only rewrites the URL on https pages, so on http (the LAN deployment) it dials the
// internal host directly and the media connection fails.
const read = p => fs.readFileSync(path.join(__dirname, '..', p), 'utf8').replace(/\r\n/g, '\n');

test('the media URL handed to browsers is browser-reachable, not the cluster-internal service', () => {
  const endpoints = read('src/IPop.Api/Endpoints/CallEndpoints.cs');
  const withToken = endpoints.slice(endpoints.indexOf('WithTokenAsync(CallRoomDto'));
  const assigned = /LiveKitUrl\s*=\s*liveKit\.(\w+)/.exec(withToken);
  assert.ok(assigned, 'WithTokenAsync must set LiveKitUrl from the LiveKit token service');
  // `ServerUrl` is Calls:LiveKit:ServerUrl (the in-cluster service); `PublicUrl` must be used instead.
  assert.equal(assigned[1], 'PublicUrl',
    `LiveKitUrl is built from liveKit.${assigned[1]}; browsers need the public address`);
});

test('the token service exposes a browser-facing URL distinct from the internal one', () => {
  const service = read('src/IPop.Infrastructure/Calls/LiveKitTokenService.cs');
  assert.match(service, /PublicUrl/,
    'LiveKitTokenService must publish a PublicUrl derived from Calls:LiveKit:Url for browser clients');
});
