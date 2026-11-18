-- Deterministic synthetic payment history, shared by wallet.sql and payments.sql.
-- Both databases compute exactly the same payments (ids, accounts, amounts, outcomes) from the
-- same integer sequence, so the wallet ledger and the payments history agree with each other.
--
-- psql variables: :accounts, :payments, :'base' (timestamp the history ends at)
--
-- Mix: ~70 % external / 30 % internal; ~2 % rejected (insufficient funds), ~3 % of external
-- payments failed (bank rejected, funds released), the rest completed. Spread over the last 90 days.

CREATE TEMP TABLE seed_pay AS
WITH base AS (
    SELECT i,
           1 + ((i::bigint * 7919) % :accounts)                     AS src,
           1 + ((i::bigint * 104729 + 13) % :accounts)              AS dst0,
           CASE WHEN i % 10 < 7 THEN 'External' ELSE 'Internal' END AS type,
           (i::bigint * 31) % 100                                   AS r,
           100 + ((i::bigint * 2654435761) % 250000)                AS amount,
           :'base'::timestamptz - interval '90 days'
             + (i::float8 / :payments) * interval '90 days'         AS created_at
    FROM generate_series(1, :payments) AS i
)
SELECT i,
       md5('pay-' || i)::uuid                                         AS id,
       md5('acct-' || src)::uuid                                      AS source_id,
       CASE WHEN type = 'Internal'
            THEN md5('acct-' || CASE WHEN dst0 = src THEN src % :accounts + 1 ELSE dst0 END)::uuid
       END                                                            AS dest_id,
       type,
       CASE WHEN r < 2 THEN 'Rejected'
            WHEN r < 5 AND type = 'External' THEN 'Failed'
            ELSE 'Completed' END                                      AS status,
       amount,
       created_at,
       created_at + CASE WHEN r < 2 THEN interval '120 ms' ELSE interval '1500 ms' END AS finished_at
FROM base;
