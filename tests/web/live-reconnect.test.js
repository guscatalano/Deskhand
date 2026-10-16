#!/usr/bin/env node
// Proves the dashboard's live/interact auto-reconnect (v0.2.86) by running the REAL shipped functions
// (extracted from wwwroot/index.html) against stubbed DOM, timers and fetch — so the test can't drift
// from what ships. No external deps; run with `node`.
'use strict';
const fs = require('fs'), path = require('path'), assert = require('assert');

const HTML = process.env.DH_HTML || path.join(__dirname, '..', '..', 'src', 'Deskhand.Http', 'wwwroot', 'index.html');
const src = fs.readFileSync(HTML, 'utf8');

// Extract the contiguous block: from `let liveOn=` up to and including the end of liveOpenH264.
const start = src.indexOf('let liveOn=false');
const anchor = src.indexOf('function liveOpenH264(', start);
assert(start >= 0 && anchor >= 0, 'could not locate the live* block in index.html');
// find the closing brace of liveOpenH264 (first line that is exactly "}" at column 0 after the anchor)
const after = src.indexOf('\n}', anchor);
assert(after >= 0, 'could not find end of liveOpenH264');
const code = src.slice(start, after + 2);
assert(/function liveReconnect\(/.test(code) && /function liveStart\(/.test(code)
  && /function liveOpenMjpeg\(/.test(code) && /function liveOpenH264\(/.test(code),
  'extracted block is missing expected functions');

// ---- controllable fake clock ----
let now = 0, timers = [], tid = 0;
function setTimeout(fn, delay){ const t={id:++tid, at:now+delay, delay, fn}; timers.push(t); return t.id; }
function clearTimeout(id){ timers = timers.filter(t => t.id !== id); }
function advance(ms){ // run due timers in order
  const end = now + ms;
  for(;;){ const due = timers.filter(t=>t.at<=end).sort((a,b)=>a.at-b.at)[0]; if(!due) break;
    timers = timers.filter(t=>t!==due); now = due.at; due.fn(); }
  now = end;
}
function pendingDelays(){ return timers.map(t=>t.delay); }

// ---- minimal DOM ----
function mkEl(tag){
  const el = { tag, children:[], style:{}, className:'', value:'', disabled:false, onload:null, onerror:null, _src:'' };
  Object.defineProperty(el, 'src', { get(){return el._src;}, set(v){ el._src=v; } });
  el.classList = { add(){}, remove(){} };
  el.querySelector = sel => el.children.find(c => matches(c, sel)) || null;
  el.querySelectorAll = sel => ({ forEach: fn => el.children.filter(c=>matches(c,sel)).slice().forEach(fn) });
  el.insertBefore = (n /*,ref*/) => { el.children.push(n); n._parent = el; };
  el.append = n => { el.children.push(n); n._parent = el; };
  el.remove = () => { if(el._parent){ el._parent.children = el._parent.children.filter(c=>c!==el); } };
  el.load = () => {};
  el.addEventListener = () => {};
  return el;
}
function matches(el, sel){
  if(sel==='img,video') return el.tag==='img' || el.tag==='video';
  if(sel==='img.live') return el.tag==='img' && el.className==='live';
  if(sel==='video.live') return el.tag==='video' && el.className==='live';
  if(sel==='img') return el.tag==='img';
  if(sel==='video') return el.tag==='video';
  if(sel==='.vempty') return el.className==='vempty';
  return false;
}
const els = {};
function $(sel){ return els[sel] || (els[sel] = mkEl(sel.replace('#',''))); }
const viewer = $('#viewer');
const vempty = mkEl('div'); vempty.className='vempty'; viewer.children.push(vempty); vempty._parent=viewer;
$('#liveCodec').value = 'mjpeg';
$('#monSel').value = '';

function Image(){ const el = mkEl('img'); return el; }
let toasts = [];
function toast(msg, isErr){ toasts.push({msg, isErr}); }
const AUTH_TOKEN = '';
const Dnow = () => now;

// expose the fns we assign via `function ...` declarations — eval in a scope carrying the stubs
const scope = { setTimeout, clearTimeout, $, Image, toast, AUTH_TOKEN,
  Date: { now: Dnow }, Math, URL:{createObjectURL:()=>'blob:x',revokeObjectURL:()=>{}},
  window:{MediaSource:undefined}, MediaSource:undefined, document:{createElement:mkEl},
  AbortController: function(){ this.abort=()=>{}; this.signal={}; }, fetch: async()=>({ok:true,body:{getReader:()=>({read:async()=>({done:true})})}}) };
const names = Object.keys(scope);
const factory = new Function(...names, code + '\n;return {liveStart,liveStop,liveReconnect,liveOpenMjpeg,get liveOn(){return liveOn;},get liveTries(){return liveTries;},get liveEpoch(){return liveEpoch;}};');
const M = factory(...names.map(n=>scope[n]));

// helpers
function currentImg(){ return viewer.children.filter(c=>c.tag==='img').slice(-1)[0]; }
let passed=0; function ok(name){ passed++; console.log('  ok -', name); }

// ---- Scenario 1: a drop while on schedules a reconnect (does NOT stop) ----
M.liveStart({inputDesktop:true});
assert.strictEqual(M.liveOn, true, 'liveOn after start');
const img1 = currentImg(); assert(img1 && /\/capture\/stream\?/.test(img1.src), 'mjpeg stream opened');
img1.onerror();
assert.strictEqual(M.liveOn, true, 'still on after a drop (not stopped)');
assert.deepStrictEqual(pendingDelays(), [500], 'first reconnect scheduled at 500ms');
ok('drop schedules a reconnect instead of stopping');

// ---- Scenario 2: reconnect actually reopens the stream ----
advance(500);
const img2 = currentImg(); assert(img2 && img2 !== img1, 'a new stream element was created on reconnect');
assert(/\/capture\/stream\?/.test(img2.src), 'reconnect reopened the MJPEG stream');
ok('reconnect reopens the stream');

// ---- Scenario 3: a stale error from the OLD element is ignored ----
img1.onerror();                 // the replaced element errors late
assert.deepStrictEqual(pendingDelays(), [], 'stale error from old connection scheduled nothing');
ok('stale error from a replaced connection is ignored (epoch guard)');

// ---- Scenario 4: backoff grows per consecutive failure, capped at 5000 ----
M.liveStop(); M.liveStart({inputDesktop:true});   // clean slate: liveTries back to 0
const seen = [];
for(let i=0;i<10;i++){ const im=currentImg(); im.onerror(); seen.push(pendingDelays()[0]); advance(pendingDelays()[0]); }
// 500,800,1280,2048,3276.8,5000(capped)...
assert.strictEqual(seen[0], 500); assert.strictEqual(seen[1], 800);
assert(Math.abs(seen[2]-1280)<1); assert(seen[seen.length-1] <= 5000, 'capped at 5000');
assert(seen.some(d=>d===5000), 'backoff reaches the 5000ms cap');
ok('backoff grows and caps at 5000ms');

// ---- Scenario 5: a healthy frame resets the backoff ----
currentImg().onload();          // bytes flowed → healthy
assert.strictEqual(M.liveTries, 0, 'liveTries reset after a healthy frame');
currentImg().onerror();
assert.deepStrictEqual(pendingDelays(), [500], 'next drop reconnects fast again (backoff reset)');
ok('a healthy frame resets the backoff');

// ---- Scenario 6: Stop cancels any pending reconnect and prevents further ones ----
M.liveStop();
assert.strictEqual(M.liveOn, false, 'stopped');
assert.deepStrictEqual(pendingDelays(), [], 'Stop cleared the pending reconnect timer');
// firing a late error after stop must not reschedule
if(img2.onerror) img2.onerror();
assert.deepStrictEqual(pendingDelays(), [], 'no reconnect after Stop');
ok('Stop cancels pending + future reconnects');

console.log(`\nALL PASSED (${passed} scenarios).`);
