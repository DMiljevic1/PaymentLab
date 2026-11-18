// PaymentLab load test. Started by ./lab.ps1 load <scenario> [rate] [duration].
//
//   steady  constant arrival rate                         (default 20/s for 5m)
//   ramp    arrival rate ramps from 5/s up to RATE; aborts when thresholds break (default 300/s over 10m)
//   spike   base rate, a 10x burst for 30s, then recovery (default 20/s; DURATION = recovery time, 2m)
//   soak    moderate constant rate for a long time        (default 15/s for 30m)
//
// One iteration = one client action: 65 % create a payment and poll it to a final state, 20 % read an account,
// 15 % list an account's payments. Metrics go to the OTel collector -> Prometheus (job="k6").
import { payment, readAccount, listPayments, prepareAccounts, seededAccounts } from './lib/client.js';

const SCENARIO = (__ENV.SCENARIO || 'steady').toLowerCase();
const DEFAULTS = {
    steady: { rate: 20, duration: '5m' },
    ramp: { rate: 300, duration: '10m' },
    spike: { rate: 20, duration: '2m' },
    soak: { rate: 15, duration: '30m' },
};
if (!DEFAULTS[SCENARIO]) throw new Error(`unknown scenario '${SCENARIO}' (steady | ramp | spike | soak)`);

const RATE = Number(__ENV.RATE || DEFAULTS[SCENARIO].rate);
const DURATION = __ENV.DURATION || DEFAULTS[SCENARIO].duration;
const PEAK = SCENARIO === 'spike' ? RATE * 10 : RATE;
// Iterations poll until the payment is final, so a VU is busy for ~1-2 s (longer when the system is slow).
// When all VUs are busy k6 reports dropped_iterations - itself a sign that the system cannot keep up.
const VUS = Math.max(20, Math.min(PEAK * 2, 300));
const MAX_VUS = Math.max(50, Math.min(PEAK * 8, Number(__ENV.MAX_VUS || 800)));

function scenario() {
    const common = { exec: 'action', timeUnit: '1s', preAllocatedVUs: VUS, maxVUs: MAX_VUS };
    switch (SCENARIO) {
        case 'steady':
        case 'soak':
            return { ...common, executor: 'constant-arrival-rate', rate: RATE, duration: DURATION };
        case 'ramp':
            return { ...common, executor: 'ramping-arrival-rate', startRate: 5, stages: [{ target: RATE, duration: DURATION }] };
        case 'spike':
            return {
                ...common, executor: 'ramping-arrival-rate', startRate: RATE,
                stages: [
                    { target: RATE, duration: '1m' },
                    { target: RATE * 10, duration: '5s' },
                    { target: RATE * 10, duration: '30s' },
                    { target: RATE, duration: '5s' },
                    { target: RATE, duration: DURATION },
                ],
            };
    }
}

const thresholds = {
    http_req_failed: [{ threshold: 'rate<0.05', abortOnFail: SCENARIO === 'ramp', delayAbortEval: '30s' }],
    'http_req_duration{name:POST /api/payments}': [{ threshold: 'p(95)<2000', abortOnFail: SCENARIO === 'ramp', delayAbortEval: '30s' }],
    payment_e2e_duration: ['p(95)<10000'],
    payments_unfinished: ['count<1'],
};

export const options = {
    scenarios: { [SCENARIO]: scenario() },
    thresholds,
    // no "url" tag: it contains ids and would explode metric cardinality ("name" groups the URLs instead)
    systemTags: ['status', 'method', 'name', 'group', 'check', 'error', 'error_code', 'scenario', 'expected_response'],
    setupTimeout: '120s',
    summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
};

export function setup() {
    const created = prepareAccounts();
    const n = created.length || seededAccounts.length;
    console.log(`scenario=${SCENARIO} rate=${RATE}/s duration=${DURATION} accounts=${n} (${created.length ? 'created' : 'seeded'}) vus=${VUS}..${MAX_VUS}`);
    return { accounts: created };
}

export function action(data) {
    const accounts = data.accounts.length ? data.accounts : seededAccounts;
    const r = Math.random();
    if (r < 0.65) payment(accounts);
    else if (r < 0.85) readAccount(accounts);
    else listPayments(accounts);
}
