-- Seeds the payments database (run by ./lab.ps1 seed, inside a single transaction).
-- Replaces all payments, sagas, history, outbox and inbox rows. All seeded sagas are in a final state.
SET synchronous_commit = off;
SET work_mem = '64MB';

TRUNCATE payments, payment_sagas, payment_history, outbox_messages, inbox_messages RESTART IDENTITY;

\ir common.sql

INSERT INTO payments (id, idempotency_key, request_hash, type, source_account_id, destination_account_id, creditor_iban,
                      amount_minor, currency, reference, status, failure_reason, bank_reference,
                      created_at, updated_at, completed_at)
SELECT id,
       'seed-' || i,
       md5('req-' || i),
       type,
       source_id,
       dest_id,
       CASE WHEN type = 'External'
            THEN 'HR' || lpad(((i * 7) % 100)::text, 2, '0') || lpad(((i::bigint * 7907) % 100000000000000000)::text, 17, '0')
       END,
       amount,
       'EUR',
       'INV-' || i,
       status,
       CASE status WHEN 'Rejected' THEN 'InsufficientFunds' WHEN 'Failed' THEN 'bank rejected: AG01' END,
       CASE WHEN type = 'External' AND status <> 'Rejected'
            THEN 'FB-' || to_char(created_at, 'YYYYMMDD') || '-S' || lpad(i::text, 8, '0')
       END,
       created_at, finished_at, finished_at
FROM seed_pay;

INSERT INTO payment_sagas (payment_id, state, current_step, completed_steps, attempt, created_at, updated_at)
SELECT id, status, NULL,
       CASE WHEN status <> 'Completed' THEN '{}'::text[]
            WHEN type = 'External' THEN '{ReserveFunds,SubmitToBank,CommitFunds}'::text[]
            ELSE '{ReserveFunds,CommitFunds}'::text[] END,
       0, created_at, finished_at
FROM seed_pay;

-- Saga transition history, following the same transitions the real saga writes.
CREATE TEMP TABLE hist_tpl (kind text, ord int, from_state text, to_state text, step text, detail text, offset_ms int);
INSERT INTO hist_tpl VALUES
  ('ExtCompleted', 1, NULL,             'Created',          NULL,           NULL,                  0),
  ('ExtCompleted', 2, 'Created',        'ReservingFunds',   'ReserveFunds', NULL,                 40),
  ('ExtCompleted', 3, 'ReservingFunds', 'AwaitingBank',     'SubmitToBank', NULL,                 90),
  ('ExtCompleted', 4, 'AwaitingBank',   'AwaitingBank',     'SubmitToBank', 'waiting up to 30s',  91),
  ('ExtCompleted', 5, 'AwaitingBank',   'CommittingFunds',  'CommitFunds',  NULL,               1400),
  ('ExtCompleted', 6, 'CommittingFunds','Completed',        NULL,           NULL,               1500),
  ('IntCompleted', 1, NULL,             'Created',          NULL,           NULL,                  0),
  ('IntCompleted', 2, 'Created',        'ReservingFunds',   'ReserveFunds', NULL,                 40),
  ('IntCompleted', 3, 'ReservingFunds', 'CommittingFunds',  'CommitFunds',  NULL,                 90),
  ('IntCompleted', 4, 'CommittingFunds','Completed',        NULL,           NULL,               1500),
  ('ExtFailed',    1, NULL,             'Created',          NULL,           NULL,                  0),
  ('ExtFailed',    2, 'Created',        'ReservingFunds',   'ReserveFunds', NULL,                 40),
  ('ExtFailed',    3, 'ReservingFunds', 'AwaitingBank',     'SubmitToBank', NULL,                 90),
  ('ExtFailed',    4, 'AwaitingBank',   'AwaitingBank',     'SubmitToBank', 'waiting up to 30s',  91),
  ('ExtFailed',    5, 'AwaitingBank',   'Compensating',     'ReserveFunds', 'bank rejected: AG01', 1400),
  ('ExtFailed',    6, 'Compensating',   'Failed',           NULL,           'bank rejected: AG01', 1500),
  ('Rejected',     1, NULL,             'Created',          NULL,           NULL,                  0),
  ('Rejected',     2, 'Created',        'ReservingFunds',   'ReserveFunds', NULL,                 40),
  ('Rejected',     3, 'ReservingFunds', 'Rejected',         NULL,           'InsufficientFunds',  120);

INSERT INTO payment_history (payment_id, from_state, to_state, step, detail, occurred_at)
SELECT p.id, t.from_state, t.to_state, t.step, t.detail, p.created_at + t.offset_ms * interval '1 ms'
FROM seed_pay p
JOIN hist_tpl t ON t.kind = CASE
        WHEN p.status = 'Rejected' THEN 'Rejected'
        WHEN p.status = 'Failed' THEN 'ExtFailed'
        WHEN p.type = 'External' THEN 'ExtCompleted'
        ELSE 'IntCompleted' END;

CREATE TABLE IF NOT EXISTS lab_seed_info (scale text, accounts int, payments int, seeded_at timestamptz);
TRUNCATE lab_seed_info;
INSERT INTO lab_seed_info VALUES (:'scale', :accounts, :payments, now());
