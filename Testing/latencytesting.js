// k6 latency test para sa API
//
// Takbo:
//   k6 run latency-test.js
//   k6 run -e TARGET_URL=https://api.eballotsacdeco.site/<actual-endpoint> latency-test.js

import http from 'k6/http';
import { check, sleep } from 'k6';

// Palitan ng actual endpoint ng API mo (hindi lang root "/")
const TARGET_URL = __ENV.TARGET_URL || 'https://api.eballotsacdeco.site/';

export const options = {
  // Ramp-up -> steady -> ramp-down, hindi biglaang 300 VUs
  stages: [
    { duration: '20s', target: 300 }, // ramp-up
    { duration: '2m', target: 300 },  // steady state: dito basahin ang latency
    { duration: '10s', target: 0 },   // ramp-down
  ],

  // Dito nagfa-fail ang test (non-zero exit code). Ayusin ang numbers ayon sa target mo.
  thresholds: {
    http_req_failed: ['rate<0.01'], // mas mababa sa 1% ang errors
    // successful responses lang ang bibilangin, para hindi mapaganda ng mabilis na errors ang numero
    'http_req_duration{expected_response:true}': ['p(95)<300', 'p(99)<800'],
  },

  // Isama ang p(99) sa end-of-test summary
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],

  // Gamitin LANG kung self-signed ang certificate:
  // insecureSkipTLSVerify: true,

  // ALTERNATIBO (fixed RPS / open model): burahin ang `stages` sa itaas at gamitin ito:
  // scenarios: {
  //   fixed_rps: {
  //     executor: 'constant-arrival-rate',
  //     rate: 200,            // requests per second
  //     timeUnit: '1s',
  //     duration: '2m',
  //     preAllocatedVUs: 100,
  //     maxVUs: 500,
  //   },
  // },
};

export default function () {
  const res = http.get(TARGET_URL, {
    tags: { name: 'api_get' }, // iisang pangalan para sa metrics ng request na ito
  });

  // Ang latency ay hinuhusgahan ng thresholds sa itaas (p95/p99), hindi ng per-request check
  check(res, {
    'status is 200': (r) => r.status === 200,
  });

  sleep(1); // think time ng bawat VU
}