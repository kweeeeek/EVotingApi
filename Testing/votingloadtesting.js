
import http from 'k6/http';
import { check, sleep } from 'k6';
import { SharedArray } from 'k6/data';
import { Counter, Trend } from 'k6/metrics';
import exec from 'k6/execution';


const env = (k, d) => (__ENV[k] !== undefined && __ENV[k] !== '' ? __ENV[k] : d);
const num = (k, d) => Number(env(k, d));

const TEST = env('TEST', 'smoke');
const BASE_URL = env('BASE_URL', 'https://api.eballotsacdeco.site').replace(/\/+$/, '');
const API = BASE_URL + env('API_PREFIX', '/api/v1');
const DEBUG = env('DEBUG', '') !== '';
const TIME_SCALE = num('TIME_SCALE', 1);
const THINK = num('THINK', TEST === 'stress' ? 0.2 : 1);
const EVENT_ID = env('EVENT_ID', '');
const WITH_REGISTRATION = env('WITH_REGISTRATION', '1') !== '0';

const VOTERS = num('VOTERS', 20000);
const WINDOW_HOURS = num('WINDOW_HOURS', 4);
const PEAK_FACTOR = num('PEAK_FACTOR', 3);
const AVG_PM = Math.ceil(VOTERS / (WINDOW_HOURS * 60));
const PEAK_PM = Math.ceil(AVG_PM * PEAK_FACTOR); 

const FULL_MINUTES = num('FULL_MINUTES', 30);
const LOAD_MINUTES = num('LOAD_MINUTES', 15);
const SOAK_HOURS = num('SOAK_HOURS', 2);
const STRESS_STEP_SEC = num('STRESS_STEP_SEC', 75);
const KIOSKS = num('KIOSKS', 50);
const KIOSK_COUNT = num('KIOSK_COUNT', 1);
const HEARTBEAT_SEC = num('HEARTBEAT_SEC', 15);
const ADMIN_VIEWERS = num('ADMIN_VIEWERS', 5);
const ADMIN_POLL_SEC = num('ADMIN_POLL_SEC', 5);
const RECEIPT_RATE = num('RECEIPT_RATE', 0.3); 
const PRE_VUS = num('PRE_VUS', 100);
const MAX_VUS = num('MAX_VUS', 1000);
const VOTER_OFFSET = num('VOTER_OFFSET', 0);
const OFFLINE_KIOSKS = num('OFFLINE_KIOSKS', 20);
const OFFLINE_BATCH = num('OFFLINE_BATCH', 100);

const voters = new SharedArray('voters', () => JSON.parse(open(env('VOTERS_FILE', './voters.json'))));
const OFFLINE_OFFSET = num('OFFLINE_OFFSET', Math.floor(voters.length / 2));

const CREDS = {
  admin: { user: env('ADMIN_USER', 'admin'), pass: env('ADMIN_PASS', '') },
  registration: { user: env('REG_USER', 'registration'), pass: env('REG_PASS', '') },
  kiosk: { user: env('KIOSK_USER', 'kiosk'), pass: env('KIOSK_PASS', '') },
};


const EP = {
  health: '/health/ready',
  login: { admin: '/auth/admin-login', registration: '/auth/registration-login', kiosk: '/auth/kiosk-login' },
  voterVerify: '/auth/voter-verify',
  eventCurrent: '/events/current',
  candidates: (id) => `/events/${id}/candidates`,
  rules: (id) => `/events/${id}/position-rules`,
  vote: '/votes',
  offlineBulk: '/votes/offline-sync-bulk',
  receipt: (code) => `/votes/receipt/${encodeURIComponent(code)}`,
  regSearch: '/registration/members/search',
  regQr: (id) => `/registration/members/${id}/qr-issued`,
  heartbeat: '/health/heartbeat',
  kiosks: '/health/kiosks',
  dashboard: '/admin/dashboard',
  turnout: (id) => `/events/${id}/turnout`,
  turnoutBranch: (id) => `/admin/turnout-by-branch/${id}`,
  monitoring: '/monitoring/overview',
};


function loginBody(role) {
  let user = CREDS[role].user;
  if (role === 'kiosk' && KIOSK_COUNT > 1) {
    user += String((exec.vu.idInTest % KIOSK_COUNT) + 1).padStart(2, '0');
  }
  return { username: user, password: CREDS[role].pass };
}


function verifyBody(voter) {
  return { code: voter.code };
}


function buildVotePayload({ eventId, candidates, rules }) {
  const ruleList = asArray(rules);
  const groups = {};
  asArray(candidates).forEach((c) => {
    const pid = find(c, ['positionId', 'position_id']);
    const cid = find(c, ['id', 'candidateId', 'candidate_id']);
    if (pid === undefined || cid === undefined) return;
    (groups[pid] = groups[pid] || { positionId: pid, ids: [] }).ids.push(cid);
  });
  const selections = Object.keys(groups).map((key) => {
    const g = groups[key];
    const rule = ruleList.find((r) => String(find(r, ['positionId', 'position_id', 'id'])) === key) || {};
    const max = Number(find(rule, ['maxVotes', 'maxSelections', 'seats', 'slots', 'max']) || 1);
    return { positionId: g.positionId, candidateIds: sample(g.ids, max) };
  });
  if (!selections.length) warnOnce('buildVotePayload: walang nabuong selections. I-adjust ito (gamitin ang -e DEBUG=1).');
  return { eventId, selections };
}


function buildOfflineBulkPayload(eventId, batch, candidates, rules) {
  return {
    eventId,
    votes: batch.map((v) =>
      Object.assign(buildVotePayload({ eventId, candidates, rules }), {
        memberId: v.id,
        offlineId: `${exec.vu.idInTest}-${v.id}`,
        castAt: new Date().toISOString(),
      })
    ),
  };
}


function heartbeatBody() {
  return { kioskId: (exec.scenario.iterationInTest % Math.max(KIOSKS, 1)) + 1, status: 'online' };
}

const votesCast = new Counter('votes_cast');
const votesRejected = new Counter('votes_rejected');
const offlineSynced = new Counter('offline_votes_synced');
const votersExhausted = new Counter('voters_exhausted');
const journeysOk = new Counter('journeys_completed');
const journeysFailed = new Counter('journeys_failed');
const tokenRefreshes = new Counter('token_refreshes'); 
const journeyServerTime = new Trend('journey_server_time', true); 


function json(res) {
  try {
    return res ? res.json() : null;
  } catch (e) {
    return null;
  }
}
function find(j, keys) {
  const roots = [j, j && j.data];
  for (let r = 0; r < roots.length; r++) {
    const root = roots[r];
    if (root && typeof root === 'object') {
      for (let i = 0; i < keys.length; i++) if (root[keys[i]] !== undefined && root[keys[i]] !== null) return root[keys[i]];
    }
  }
  return undefined;
}
function asArray(x) {
  const v = x && !Array.isArray(x) && x.data !== undefined ? x.data : x;
  if (Array.isArray(v)) return v;
  if (v && typeof v === 'object') {
    const ks = ['items', 'candidates', 'positions', 'rules', 'results'];
    for (let i = 0; i < ks.length; i++) if (Array.isArray(v[ks[i]])) return v[ks[i]];
  }
  return [];
}
function sample(arr, n) {
  const a = arr.slice();
  for (let i = a.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    const t = a[i];
    a[i] = a[j];
    a[j] = t;
  }
  return a.slice(0, n);
}
const isOk = (res, ...codes) => res !== null && res !== undefined && (codes.length ? codes.includes(res.status) : res.status >= 200 && res.status < 300);
const think = (min, max) => sleep((min + Math.random() * (max - min)) * THINK);

let warned = 0; 
function warnOnce(msg) {
  if (warned++ < 3) console.warn(msg);
}
function dbg(label, res) {
  if (!DEBUG || exec.scenario.iterationInTest > 1) return;
  console.log(`[debug] ${label} -> ${res ? res.status : 'walang response'} ${res ? String(res.body).slice(0, 300) : ''}`);
}

let serverTime = 0; 
const OK_OR_401 = http.expectedStatuses({ min: 200, max: 399 }, 401);
function request(method, path, body, endpoint, token, tolerate401) {
  const headers = { 'Content-Type': 'application/json', Accept: 'application/json' };
  if (token) headers.Authorization = `Bearer ${token}`;
  const params = { headers, tags: { endpoint }, timeout: '30s' };
  if (tolerate401) params.responseCallback = OK_OR_401; 
  const res = http.request(method, API + path, body ? JSON.stringify(body) : null, params);
  serverTime += res.timings.duration;
  return res;
}

function extractToken(res) {
  return find(json(res), ['token', 'accessToken', 'access_token', 'jwt']) || '';
}


const tokens = {};
function login(role) {
  const res = request('POST', EP.login[role], loginBody(role), `login_${role}`);
  const ok = isOk(res, 200);
  check(ok, { [`login ${role} OK`]: (v) => v === true });
  tokens[role] = ok ? extractToken(res) : undefined;
  if (!ok) warnOnce(`login ${role} FAILED status=${res.status} body=${String(res.body).slice(0, 200)}`);
  return ok;
}

function call(role, method, path, body, endpoint, override) {
  if (override !== undefined) return request(method, path, body, endpoint, override);
  if (tokens[role] === undefined && !login(role)) return null;
  let res = request(method, path, body, endpoint, tokens[role], true);
  if (res.status === 401) {
    tokenRefreshes.add(1);
    tokens[role] = undefined;
    if (!login(role)) return res;
    res = request(method, path, body, endpoint, tokens[role], false); 
  }
  return res;
}

function step(name, res, ...okCodes) {
  const ok = isOk(res, ...okCodes);
  check(ok, { [name]: (v) => v === true });
  if (!ok) {
    journeysFailed.add(1);
    warnOnce(`x ${name}: status=${res ? res.status : 'walang response'} body=${res ? String(res.body).slice(0, 200) : ''}`);
  }
  return ok;
}


function profile() {
  switch (TEST) {
    case 'load':
      return { start: 0, stages: [[PEAK_PM, 120], [PEAK_PM, LOAD_MINUTES * 60], [0, 60]] };
    case 'full': {
      const rate = Math.ceil(VOTERS / FULL_MINUTES);
      return { start: 0, stages: [[rate, 60], [rate, FULL_MINUTES * 60 - 30]] }; // ~VOTERS na iterations sa kabuuan
    }
    case 'stress': {
      const ladder = [300, 600, 1200, 2400, 3600, 4800]; // 5, 10, 20, 40, 60, 80 voters/sec
      const stages = [];
      ladder.forEach((r) => stages.push([r, 15], [r, STRESS_STEP_SEC]));
      return { start: 0, stages };
    }
    case 'spike':
      return { start: 120, stages: [[120, 60], [1800, 10], [1800, 120], [120, 10], [120, 180]] }; // 2/s -> 30/s -> 2/s
    case 'soak':
      return { start: 0, stages: [[AVG_PM, 120], [AVG_PM, SOAK_HOURS * 3600], [0, 60]] };
    case 'offline':
      return { start: 0, stages: [[120, 30], [120, 240]] }; // may live voting habang nagsi-sync ang mga kiosk
    default:
      return null; 
  }
}
const scaleSec = (s) => Math.max(1, Math.round(s * TIME_SCALE));

function buildScenarios() {
  const p = profile();
  const sc = {};
  if (!p) {
    sc.smoke = { executor: 'shared-iterations', exec: 'voterJourney', vus: 1, iterations: 5, maxDuration: '5m' };
    return sc;
  }
  const stages = p.stages.map(([target, sec]) => ({ target, sec: scaleSec(sec) }));
  const total = stages.reduce((a, s) => a + s.sec, 0);
  sc.voter_journey = {
    executor: 'ramping-arrival-rate',
    exec: 'voterJourney',
    startRate: p.start,
    timeUnit: '1m',
    preAllocatedVUs: PRE_VUS,
    maxVUs: MAX_VUS,
    stages: stages.map((s) => ({ target: s.target, duration: `${s.sec}s` })),
  };
  if (KIOSKS > 0) {
    sc.kiosk_heartbeat = {
      executor: 'constant-arrival-rate',
      exec: 'kioskHeartbeat',
      rate: KIOSKS,
      timeUnit: `${HEARTBEAT_SEC}s`,
      duration: `${total}s`,
      preAllocatedVUs: 5,
      maxVUs: 50,
    };
  }
  if (ADMIN_VIEWERS > 0) {
    sc.admin_watch = { executor: 'constant-vus', exec: 'adminWatch', vus: ADMIN_VIEWERS, duration: `${total}s` };
  }
  if (TEST === 'offline') {
    sc.offline_burst = {
      executor: 'shared-iterations',
      exec: 'offlineBurst',
      vus: OFFLINE_KIOSKS,
      iterations: OFFLINE_KIOSKS,
      maxDuration: '10m',
      startTime: `${scaleSec(60)}s`,
    };
  }
  return sc;
}

function estimateIterations(p) {
  let prev = p.start;
  let sum = 0;
  p.stages.forEach(([target, sec]) => {
    sum += ((prev + target) / 2) * ((sec * TIME_SCALE) / 60);
    prev = target;
  });
  return Math.round(sum);
}


const thresholds = {
  http_req_failed: ['rate<0.01'], // < 1% error
  checks: ['rate>0.99'],
  voters_exhausted: ['count==0'], // naubos ang voters.json -> dagdagan
  'http_req_duration{endpoint:vote_cast}': ['p(95)<800', 'p(99)<1500'],
  'http_req_duration{endpoint:voter_verify}': ['p(95)<500'],
  'http_req_duration{endpoint:ballot_candidates}': ['p(95)<400'],
  'http_req_duration{endpoint:reg_search}': ['p(95)<500'],
  'http_req_duration{endpoint:reg_qr_issued}': ['p(95)<500'],
  journey_server_time: ['p(95)<3000'],
};
if (TEST !== 'smoke' && ADMIN_VIEWERS > 0) thresholds['http_req_duration{endpoint:admin_dashboard}'] = ['p(95)<1500'];
if (TEST !== 'smoke' && KIOSKS > 0) thresholds['http_req_duration{endpoint:kiosk_heartbeat}'] = ['p(95)<300'];
if (TEST === 'offline') thresholds['http_req_duration{endpoint:offline_sync_bulk}'] = ['p(95)<5000'];
if (TEST === 'stress') {
  thresholds.http_req_failed = [{ threshold: 'rate<0.05', abortOnFail: true, delayAbortEval: '60s' }];
  thresholds.checks = ['rate>0.95'];
} else {
  thresholds.dropped_iterations = ['count==0']; 
  thresholds.votes_rejected = ['count==0'];
}

export const options = {
  scenarios: buildScenarios(),
  thresholds,
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  insecureSkipTLSVerify: env('INSECURE', '') !== '',
};


export function setup() {
  const r = http.get(API + EP.health, { tags: { endpoint: 'health_ready' } });
  if (r.status !== 200) {
    exec.test.abort(`Hindi pasado ang health check (${API}${EP.health} -> ${r.status}). Tama ba ang BASE_URL / API_PREFIX?`);
  }
  const p = profile();
  const est = p ? estimateIterations(p) : 5;
  const avail = voters.length - VOTER_OFFSET;
  console.log(
    `TEST=${TEST} | voters sa file=${voters.length} | tinatayang voters na gagamitin=${est} | ` +
      `avg=${AVG_PM}/min peak=${PEAK_PM}/min (~${(PEAK_PM / 60).toFixed(1)}/sec) | THINK=${THINK}`
  );
  if (est > avail) {
    console.warn(`! Kulang ang voters.json: kailangan ~${est}, available ${avail}. Dagdagan, o bawasan ang tagal/rate.`);
  }
}


export function voterJourney() {
  serverTime = 0;
  const idx = VOTER_OFFSET + exec.scenario.iterationInTest; // unique kada iteration -> iba-ibang voter
  if (idx >= voters.length) {
    votersExhausted.add(1);
    return;
  }
  const voter = voters[idx];

  
  if (WITH_REGISTRATION) {
    let r = call('registration', 'GET', `${EP.regSearch}?q=${encodeURIComponent(voter.code)}`, null, 'reg_search');
    dbg('reg_search', r);
    if (!step('registration search OK', r, 200)) return;
    think(1, 3);
    r = call('registration', 'POST', EP.regQr(voter.id), null, 'reg_qr_issued');
    dbg('reg_qr_issued', r);
    if (!step('qr issued OK', r, 200, 201, 204)) return;
    think(2, 5); 
  }

 
  let r = call('kiosk', 'GET', EP.eventCurrent, null, 'event_current');
  dbg('event_current', r);
  if (!step('current event OK', r, 200)) return;
  const eventId = EVENT_ID || find(json(r), ['id', 'eventId']);

  r = call('kiosk', 'POST', EP.voterVerify, verifyBody(voter), 'voter_verify');
  dbg('voter_verify', r);
  if (!step('voter verify OK', r, 200)) return;
  const voterToken = extractToken(r);
  const as = voterToken ? voterToken : undefined;

  
  r = call('kiosk', 'GET', EP.candidates(eventId), null, 'ballot_candidates', as);
  dbg('candidates', r);
  if (!step('candidates OK', r, 200)) return;
  const candidates = json(r);
  r = call('kiosk', 'GET', EP.rules(eventId), null, 'ballot_rules', as);
  dbg('position_rules', r);
  if (!step('position rules OK', r, 200)) return;
  const rules = json(r);

  think(3, 8); 

  
  r = call('kiosk', 'POST', EP.vote, buildVotePayload({ eventId, candidates, rules }), 'vote_cast', as);
  dbg('vote_cast', r);
  if (!step('vote accepted', r, 200, 201)) {
    votesRejected.add(1);
    return;
  }
  votesCast.add(1);

  
  const receipt = find(json(r), ['receiptCode', 'receipt', 'code', 'referenceCode']);
  if (receipt && Math.random() < RECEIPT_RATE) {
    think(1, 3);
    const rr = request('GET', EP.receipt(receipt), null, 'receipt_lookup'); 
    check(rr, { 'receipt lookup OK': (x) => x.status === 200 });
  }

  journeysOk.add(1);
  journeyServerTime.add(serverTime);
}


export function kioskHeartbeat() {
  const r = call('kiosk', 'POST', EP.heartbeat, heartbeatBody(), 'kiosk_heartbeat');
  check(r, { 'heartbeat OK': (x) => isOk(x, 200, 201, 204) });
}


let cachedEventId = EVENT_ID;
export function adminWatch() {
  if (!cachedEventId) {
    const r = call('admin', 'GET', EP.eventCurrent, null, 'event_current');
    cachedEventId = find(json(r), ['id', 'eventId']);
  }
  const calls = [['admin_dashboard', EP.dashboard], ['admin_kiosks', EP.kiosks], ['admin_monitoring', EP.monitoring]];
  if (cachedEventId) {
    calls.push(['admin_turnout', EP.turnout(cachedEventId)], ['admin_turnout_branch', EP.turnoutBranch(cachedEventId)]);
  }
  calls.forEach(([tag, path]) => {
    const r = call('admin', 'GET', path, null, tag);
    check(r, { [`${tag} OK`]: (x) => isOk(x, 200) });
    sleep(0.5);
  });
  sleep(ADMIN_POLL_SEC);
}


export function offlineBurst() {
  const start = OFFLINE_OFFSET + exec.scenario.iterationInTest * OFFLINE_BATCH;
  if (start + OFFLINE_BATCH > voters.length) {
    votersExhausted.add(1);
    return;
  }
  let r = call('kiosk', 'GET', EP.eventCurrent, null, 'event_current');
  if (!step('offline: current event OK', r, 200)) return;
  const eventId = EVENT_ID || find(json(r), ['id', 'eventId']);
  const candidates = json(call('kiosk', 'GET', EP.candidates(eventId), null, 'ballot_candidates'));
  const rules = json(call('kiosk', 'GET', EP.rules(eventId), null, 'ballot_rules'));

  const batch = [];
  for (let i = 0; i < OFFLINE_BATCH; i++) batch.push(voters[start + i]);
  r = call('kiosk', 'POST', EP.offlineBulk, buildOfflineBulkPayload(eventId, batch, candidates, rules), 'offline_sync_bulk');
  dbg('offline_sync_bulk', r);
  if (step('offline bulk sync OK', r, 200, 201, 202)) offlineSynced.add(OFFLINE_BATCH);
}