#!/bin/sh
# SPOILERS (chaos scenario 2): keeps a transaction open with row locks on ~20% of the accounts.
PGAPPNAME=nightly-report psql -U wallet -d wallet -q <<'SQL'
BEGIN;
SELECT count(*) FROM (SELECT id FROM accounts WHERE abs(hashtext(id::text)) % 5 = 0 FOR UPDATE) locked;
\! sleep 86400
SQL
