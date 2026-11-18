// Client behaviour shared by all scenarios: timeouts, retries with the same Idempotency-Key, polling.
import http from 'k6/http';
import { sleep } from 'k6';
import crypto from 'k6/crypto';
import { Counter, Trend } from 'k6/metrics';
import { SharedArray } from 'k6/data';

export const BASE = __ENV.BASE_URL || 'http://gateway:8080';
const CLIENT_TIMEOUT = __ENV.CLIENT_TIMEOUT || '5s';
const MAX_RETRIES = Number(__ENV.MAX_RETRIES || 3);
const POLL_INTERVAL_S = Number(__ENV.POLL_INTERVAL_S || 0.5);
const POLL_TIMEOUT_S = Number(__ENV.POLL_TIMEOUT_S || 30);
const CLIENTS = Number(__ENV.CLIENTS || 20); // distinct X-Client-Id values (the gateway rate-limits per client)

export const paymentE2E = new Trend('payment_e2e_duration', true);
export const paymentsFinal = new Counter('payments_final');
export const paymentsUnfinished = new Counter('payments_unfinished');
export const paymentCreateFailed = new Counter('payment_create_failed');
export const clientRetries = new Counter('client_retries');

const clientId = () => `k6-client-${__VU % CLIENTS}`;

function retryable(res) {
    return res.status === 0 || res.status === 429 || res.status >= 500;
}

function backoffSeconds(attempt, res) {
    if (res.status === 429) {
        const ra = Number(res.headers['Retry-After']);
        if (ra > 0) return ra;
    }
    return 0.5 * 2 ** attempt * (0.8 + Math.random() * 0.4);
}

/** HTTP call with client timeout and retries (timeouts, 429, 5xx). POSTs are retried with the same Idempotency-Key. */
export function send(method, path, body, name, idempotencyKey) {
    const headers = { 'Content-Type': 'application/json', 'X-Client-Id': clientId() };
    if (idempotencyKey) headers['Idempotency-Key'] = idempotencyKey;
    const params = { headers, timeout: CLIENT_TIMEOUT, tags: { name } };
    let res;
    for (let attempt = 0; ; attempt++) {
        res = http.request(method, BASE + path, body ? JSON.stringify(body) : null, params);
        if (!retryable(res) || attempt >= MAX_RETRIES) return res;
        clientRetries.add(1, { name, reason: res.status === 0 ? 'timeout_or_network' : String(res.status) });
        sleep(backoffSeconds(attempt, res));
    }
}

const rnd = (min, max) => min + Math.floor(Math.random() * (max - min + 1));
const pick = (arr) => arr[Math.floor(Math.random() * arr.length)];

function iban() {
    let digits = '';
    for (let i = 0; i < 17; i++) digits += rnd(0, 9);
    return `HR${rnd(10, 99)}${digits}`;
}

/** Creates a payment (70 % external, 30 % internal) and polls it until it reaches a final state. */
export function payment(accounts) {
    const external = Math.random() < 0.7;
    const source = pick(accounts);
    let dest = pick(accounts);
    while (!external && dest === source && accounts.length > 1) dest = pick(accounts);
    const type = external ? 'External' : 'Internal';
    const body = external
        ? { type, sourceAccountId: source, creditorIban: iban(), amountMinor: rnd(100, 5000), currency: 'EUR', reference: `k6-${__VU}-${__ITER}` }
        : { type, sourceAccountId: source, destinationAccountId: dest, amountMinor: rnd(100, 5000), currency: 'EUR', reference: `k6-${__VU}-${__ITER}` };

    const started = Date.now();
    const res = send('POST', '/api/payments', body, 'POST /api/payments', crypto.md5(`${__VU}-${__ITER}-${started}-${Math.random()}`, 'hex'));
    if (res.status !== 202 && res.status !== 200) {
        paymentCreateFailed.add(1, { type, status: String(res.status) });
        return;
    }
    const id = res.json('id');

    const deadline = started + POLL_TIMEOUT_S * 1000;
    while (Date.now() < deadline) {
        sleep(POLL_INTERVAL_S);
        const r = send('GET', `/api/payments/${id}`, null, 'GET /api/payments/{id}');
        if (r.status !== 200) continue;
        const status = r.json('status');
        if (status !== 'Pending') {
            paymentE2E.add(Date.now() - started, { type, status });
            paymentsFinal.add(1, { type, status });
            return;
        }
    }
    paymentsUnfinished.add(1, { type });
}

export function readAccount(accounts) {
    send('GET', `/api/accounts/${pick(accounts)}`, null, 'GET /api/accounts/{id}');
}

export function listPayments(accounts) {
    send('GET', `/api/payments?accountId=${pick(accounts)}&pageSize=20`, null, 'GET /api/payments?accountId');
}

function seededAccountId(i) {
    const h = crypto.md5(`acct-${i}`, 'hex');
    return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

const SEEDED = Number(__ENV.SEEDED_ACCOUNTS || 0);

// After ./lab.ps1 seed: a random sample of the seeded accounts (they have long histories). Built once and
// shared by all VUs (SharedArray), so a large pool does not cost memory per VU.
export const seededAccounts = new SharedArray('seeded-accounts', () => {
    if (SEEDED <= 0) return [];
    const ids = new Set();
    while (ids.size < Math.min(Number(__ENV.ACCOUNT_POOL || 2000), SEEDED)) ids.add(seededAccountId(rnd(1, SEEDED)));
    return [...ids];
});

/**
 * Accounts used by the test: the seeded sample if the databases were seeded, otherwise a fresh set of
 * well-funded accounts created through the API in setup().
 */
export function prepareAccounts() {
    if (seededAccounts.length > 0) return [];
    const size = Number(__ENV.ACCOUNT_POOL || 200);
    const reqs = [];
    for (let i = 0; i < size; i++) {
        reqs.push(['POST', `${BASE}/api/accounts`,
            JSON.stringify({ ownerName: `k6 load ${i}`, currency: 'EUR', initialBalanceMinor: 1000000000 }),
            { headers: { 'Content-Type': 'application/json', 'X-Client-Id': 'k6-setup' }, tags: { name: 'POST /api/accounts' } }]);
    }
    const ids = [];
    for (let i = 0; i < reqs.length; i += 50) {
        for (const r of http.batch(reqs.slice(i, i + 50))) {
            if (r.status === 201) ids.push(r.json('id'));
        }
    }
    if (ids.length < 2) throw new Error(`could not create accounts (gateway reachable at ${BASE}?)`);
    return ids;
}
