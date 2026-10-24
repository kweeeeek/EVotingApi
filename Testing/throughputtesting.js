// k6 throughput test (open model)
// Ikaw ang nagse-set ng req/s; ang sinusukat ay kung pasado pa rin ang latency at errors sa rate na iyon.
//
// Takbo:
//   k6 run throughput-test.js                      # constant: kaya ba ng API ang TARGET_RPS?
//   k6 run -e TARGET_RPS=300 throughput-test.js
//   k6 run -e MODE=breakpoint throughput-test.js   # unti-unting taas ng RPS hanggang mag-breach ang SLO
//   k6 run -e TARGET_URL=https://api.eballotsacdeco.site/<actual-endpoint> throughput-test.js
//
// BABALA: huwag patakbuhin sa production habang may aktibong gumagamit.

import http from 'k6/http';
import { check } from 'k6';

const TARGET_URL = __ENV.TARGET_URL || 'https://api.eballotsacdeco.site/';
const MODE = __ENV.MODE || 'constant';
const TARGET_RPS = Number(__ENV.TARGET_RPS || 200);
const DURATION = __ENV.DURATION || '2m';

// Mga hakbang ng breakpoint mode: bawat RPS ay 10s akyat + 50s hold (1 minuto bawat hakbang)
const STEPS = [100, 200, 400, 800, 1600];
const stages = [];
for (const rate of STEPS) {
  stages.push({ target: rate, duration: '10s' }); // akyat
  stages.push({ target: rate, duration: '50s' }); // hold: dito sinusukat
}

// Pag-size ng VUs (Little's Law): kailangang VUs ~ RPS x response time (segundo).
// Hal. 200 RPS x 0.3s = 60 VUs. Maglagay ng buffer; mas mataas ang maxVUs para may espasyo kapag bumagal ang server.
const scenarios = {
  constant: {
    executor: 'constant-arrival-rate',
    rate: TARGET_RPS,
    timeUnit: '1s',
    duration: DURATION,
    preAllocatedVUs: 100,
    maxVUs: 500,
  },
  breakpoint: {
    executor: 'ramping-arrival-rate',
    startRate: 50,
    timeUnit: '1s',
    preAllocatedVUs: 200,
    maxVUs: 2000,
    stages,
  },
};

if (!scenarios[MODE]) {
  throw new Error(`Hindi kilalang MODE "${MODE}". Gamitin ang "constant" o "breakpoint".`);
}

// Sa breakpoint mode, ihihinto agad ang test sa unang breach ng SLO
const t = (threshold) =>
  MODE === 'breakpoint' ? { threshold, abortOnFail: true, delayAbortEval: '10s' } : threshold;

export const options = {
  scenarios: { load: scenarios[MODE] },
  thresholds: {
    http_req_failed: [t('rate<0.01')],
    'http_req_duration{expected_response:true}': [t('p(95)<300')],
    // Kapag may dropped iterations, hindi naabot ni k6 ang target RPS (naubos ang maxVUs o bottleneck ang k6 machine)
    dropped_iterations: ['count==0'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

export default function () {
  const res = http.get(TARGET_URL, { tags: { name: 'api_get' } });

  check(res, { 'status is 200': (r) => r.status === 200 });

  // WALANG sleep() dito: iisang request kada iteration, ang executor ang nagpa-pace
}