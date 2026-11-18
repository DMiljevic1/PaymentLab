// SPOILERS (chaos scenario 14): business customers submitting salary batches - many payments from one
// account at the same moment, more than the balance covers.
import http from 'k6/http';
import { sleep } from 'k6';
import crypto from 'k6/crypto';

const BASE = __ENV.BASE_URL || 'http://gateway:8080';
const BATCH = Number(__ENV.BATCH || 30);
const BALANCE = 5000;   // covers 10 payments of 500
const AMOUNT = 500;
const json = { 'Content-Type': 'application/json', 'X-Client-Id': 'payroll-api' };

export const options = {
    scenarios: { payroll: { executor: 'constant-vus', vus: Number(__ENV.VUS || 3), duration: __ENV.DURATION || '6h' } },
    systemTags: ['status', 'method', 'name', 'scenario', 'expected_response'],
};

function account(owner, balance) {
    const r = http.post(`${BASE}/api/accounts`, JSON.stringify({ ownerName: owner, currency: 'EUR', initialBalanceMinor: balance }),
        { headers: json, tags: { name: 'POST /api/accounts' } });
    return r.status === 201 ? r.json('id') : null;
}

export default function () {
    const employer = account(`Employer ${__VU}-${__ITER}`, BALANCE);
    const employee = account(`Employee ${__VU}-${__ITER}`, 0);
    if (!employer || !employee) { sleep(5); return; }
    const reqs = [];
    for (let i = 0; i < BATCH; i++) {
        reqs.push(['POST', `${BASE}/api/payments`,
            JSON.stringify({ type: 'Internal', sourceAccountId: employer, destinationAccountId: employee, amountMinor: AMOUNT, currency: 'EUR', reference: `salary ${i}` }),
            { headers: Object.assign({ 'Idempotency-Key': crypto.md5(`${employer}-${i}`, 'hex') }, json), tags: { name: 'POST /api/payments' } }]);
    }
    http.batch(reqs);
    sleep(20);
}
