// SPOILERS (chaos scenario 15): an integration partner polling the API hard, with a new X-Client-Id per request.
import http from 'k6/http';

const BASE = __ENV.BASE_URL || 'http://gateway:8080';
const RATE = Number(__ENV.RATE || 250);

export const options = {
    scenarios: {
        partner: { executor: 'constant-arrival-rate', rate: RATE, timeUnit: '1s', duration: __ENV.DURATION || '6h',
                   preAllocatedVUs: 100, maxVUs: 300 },
    },
    systemTags: ['status', 'method', 'name', 'scenario', 'expected_response'],
};

export function setup() {
    const ids = [];
    for (let i = 0; i < 50; i++) {
        const r = http.post(`${BASE}/api/accounts`, JSON.stringify({ ownerName: `Partner customer ${i}`, currency: 'EUR', initialBalanceMinor: 100000 }),
            { headers: { 'Content-Type': 'application/json', 'X-Client-Id': 'partner-setup' } });
        if (r.status === 201) ids.push(r.json('id'));
    }
    return { ids };
}

export default function (data) {
    const id = data.ids[Math.floor(Math.random() * data.ids.length)];
    const headers = { 'X-Client-Id': `partner-${Math.random().toString(36).slice(2)}` };
    if (Math.random() < 0.5) http.get(`${BASE}/api/accounts/${id}`, { headers, tags: { name: 'GET /api/accounts/{id}' }, timeout: '10s' });
    else http.get(`${BASE}/api/payments?accountId=${id}&pageSize=100`, { headers, tags: { name: 'GET /api/payments?accountId' }, timeout: '10s' });
}
