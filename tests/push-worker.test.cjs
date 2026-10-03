const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const source = fs.readFileSync(path.join(__dirname, '../src/IPop.Web/wwwroot/sw.js'), 'utf8');
let now = 100000, windows = [], shown = [], notifications = [], cache = new Map();
const events = {};
async function message(data) { let task, reply; events.message({data,source:{url:'https://ipop.test/dashboard'},ports:[{postMessage(r){reply=r;}}],waitUntil(p){task=p;}}); await task; return reply; }
const context = { URL, Response, Date: class extends Date { static now() { return now; } },
  caches: { async open() { return { async match(k) { const r = cache.get(k); return r ? r.clone() : null; }, async put(k,r) { cache.set(k,r); }, async keys() { return [...cache.keys()].map(url=>({url})); }, async delete(k) { return cache.delete(k.url ?? k); } }; } },
  self: { location: {origin:'https://ipop.test'}, addEventListener(n,h) { events[n] = h; }, skipWaiting() {},
    clients: { async matchAll() { return windows; }, async claim() {} },
    registration: { scope: 'https://ipop.test/', async getNotifications() { return notifications; }, async showNotification(title, options) { shown.push({title, options}); } }
  }
};
vm.createContext(context);vm.runInContext(source, context);
async function push(d) { let task; events.push({data:{json:()=>d},waitUntil(p){task=p;}}); await task; }
(async()=>{
  windows = [{focused:true,visibilityState:'visible'}];
  await push({kind:'chat',tag:'chat:a',title:'A'}); assert.equal(shown.length,0);
  windows = [{focused:false,visibilityState:'hidden'}];
  await push({kind:'chat',tag:'chat:a',title:'A'}); assert.equal(shown.length,1);assert.equal(shown[0].options.silent,false);assert.equal(shown[0].options.renotify,true);
  now += 1000;notifications = [{data:{more:0}}];await push({kind:'chat',tag:'chat:a',title:'A'});
  assert.equal(shown.at(-1).options.silent,true);assert.equal(shown.at(-1).options.renotify,false);
  now += 30000;await push({kind:'chat',tag:'chat:a',title:'A'});assert.equal(shown.at(-1).options.silent,false);
  windows=[];await push({kind:'call',tag:'call:a',title:'Call',requireInteraction:true});assert.equal(shown.at(-1).options.renotify,true);assert.equal(shown.at(-1).options.silent,false);
  windows=[{focused:true,visibilityState:'visible'}];await push({kind:'test',title:'Test'});assert.equal(shown.at(-1).title,'Test');
  let repaired=false,task;windows=[{postMessage(m){repaired=m.type==='ipop-push-repair';}}];events.pushsubscriptionchange({waitUntil(p){task=p;}});await task;assert.ok(repaired);
  notifications=[];
  const reply=await message({type:'ipop-push-binding',v:1,acct:'a'.repeat(16),gen:100,optedOut:false});assert.equal(reply.accepted,true);
  const before=shown.length;windows=[];await push({kind:'chat',acct:'b'.repeat(16),tag:'chat:b',title:'Private'});assert.equal(shown.length,before);
  await message({type:'ipop-push-preferences',v:1,soundEnabled:false,volume:1});await push({kind:'chat',acct:'a'.repeat(16),tag:'chat:c',title:'A'});assert.equal(shown.at(-1).options.silent,true);
  await message({type:'ipop-push-binding',v:1,acct:null,gen:101,optedOut:true});await push({kind:'test',acct:'a'.repeat(16),title:'Stale'});assert.equal(shown.at(-1).title,'A');
  const stale=await message({type:'ipop-push-binding',v:1,acct:'a'.repeat(16),gen:99,optedOut:false});assert.equal(stale.accepted,false);
  await message({type:'ipop-push-binding',v:1,acct:'a'.repeat(16),gen:102,optedOut:false});
  context.self.registration.showNotification=async()=>{throw new Error('OS denied');};
  await push({kind:'test',acct:'a'.repeat(16),title:'Test'});
  const health=await message({type:'ipop-push-hello',v:1});assert.equal(health.lastResult.outcome,'notification-failed');
  for(let i=0;i<105;i++)await vm.runInContext(`pushState('alert:room${i}',{at:Date.now()})`,context);
  assert.ok([...cache.keys()].filter(k=>k.includes('alert%3A')).length<=100);
  console.log('Push worker tests passed: focus/background/cooldown, calls, repair, account privacy, preferences, failure diagnostics, bounded state.');
})().catch(e=>{console.error(e);process.exitCode=1;});
