// SPOILERS (chaos scenario 5): back-office screens polling payment lists by status / without filter.
import http from 'k6/http';
import { sleep } from 'k6';

const BASE = __ENV.BASE_URL || 'http://gateway:8080';
const STATUSES = ['Failed', 'Rejected', 'Completed', 'UnderReview', 'Pending'];

export const options = {
    scenarios: { ui: { executor: 'constant-vus', vus: Number(__ENV.VUS || 6), duration: __ENV.DURATION || '6h' } },
    systemTags: ['status', 'method', 'name', 'scenario', 'expected_response'],
};

export default function () {
    const page = 1 + Math.floor(Math.random() * 20);
    const url = Math.random() < 0.7
        ? `${BASE}/api/payments?status=${STATUSES[Math.floor(Math.random() * STATUSES.length)]}&page=${page}&pageSize=50`
        : `${BASE}/api/payments?page=${page}&pageSize=50`;
    http.get(url, { headers: { 'X-Client-Id': 'backoffice-ui' }, tags: { name: 'GET /api/payments (list)' }, timeout: '60s' });
    sleep(1);
}
