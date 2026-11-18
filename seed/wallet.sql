-- Seeds the wallet database (run by ./lab.ps1 seed, inside a single transaction).
-- Replaces all accounts, reservations and ledger entries.
SET synchronous_commit = off;
SET work_mem = '64MB';

TRUNCATE accounts, reservations, ledger_entries RESTART IDENTITY;

\ir common.sql

INSERT INTO accounts (id, owner_name, currency, balance_minor, reserved_minor, created_at, updated_at)
SELECT md5('acct-' || a)::uuid, 'Seed customer ' || a, 'EUR', 0, 0,
       :'base'::timestamptz - interval '120 days', :'base'::timestamptz - interval '120 days'
FROM generate_series(1, :accounts) AS a;

-- One reservation per payment that got as far as reserving funds.
INSERT INTO reservations (payment_id, account_id, amount_minor, currency, status, credit_account_id, created_at, updated_at)
SELECT id, source_id, amount, 'EUR',
       CASE status WHEN 'Completed' THEN 'Committed' ELSE 'Released' END,
       CASE WHEN status = 'Completed' THEN dest_id END,
       created_at + interval '50 ms', finished_at
FROM seed_pay
WHERE status IN ('Completed', 'Failed');

-- Debits (and credits for internal transfers) of completed payments.
INSERT INTO ledger_entries (account_id, payment_id, amount_minor, type, created_at)
SELECT source_id, id, -amount, 'Debit', finished_at FROM seed_pay WHERE status = 'Completed'
UNION ALL
SELECT dest_id, id, amount, 'Credit', finished_at FROM seed_pay WHERE status = 'Completed' AND type = 'Internal';

-- Opening deposit per account: covers every later debit plus 100 000.00 EUR, so balances never go negative.
INSERT INTO ledger_entries (account_id, payment_id, amount_minor, type, created_at)
SELECT a.id, NULL, coalesce(d.debits, 0) + 10000000, 'Deposit', a.created_at
FROM accounts a
LEFT JOIN (SELECT account_id, -sum(amount_minor) AS debits FROM ledger_entries WHERE type = 'Debit' GROUP BY account_id) d
       ON d.account_id = a.id;

-- balance = sum(ledger), reserved = 0 (no payment is in flight in the seeded history)
UPDATE accounts a
SET balance_minor = l.total, updated_at = :'base'::timestamptz
FROM (SELECT account_id, sum(amount_minor) AS total FROM ledger_entries GROUP BY account_id) l
WHERE l.account_id = a.id;

CREATE TABLE IF NOT EXISTS lab_seed_info (scale text, accounts int, payments int, seeded_at timestamptz);
TRUNCATE lab_seed_info;
INSERT INTO lab_seed_info VALUES (:'scale', :accounts, :payments, now());
