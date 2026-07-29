-- ─────────────────────────────────────────────────────────────────────────────
-- RepairEnergyHistoryGap.sql
--
-- Fills the EnergyHistory gap caused by the GetNextQuarterlyInfoInPlan() regression
-- (BatteriesService wrote a QuarterlyMeasurement for the NEXT quarter, which made
-- EnergyMonitorService skip that quarter and never store the meter readings).
--
-- WHAT IT DOES
--   For every 15-minute slot between the last stored reading before the gap and the
--   first reading after it, a row is reconstructed:
--     * the SHAPE (how much is consumed/produced per quarter) comes from the same
--       time-of-day in the days before the gap, matched on weekday vs weekend;
--     * the shape is then SCALED so the cumulative counters land exactly on the
--       measured values of the closing anchor row.
--   So totals over the gap are exact; the distribution inside the gap is an estimate.
--
-- THESE ROWS ARE RECONSTRUCTED, NOT MEASURED. They feed EnergyStatisticsService and
-- FinancialResultsService, so per-quarter figures in that window are approximations.
--
-- Time is compared as plain text: EnergyHistory.Time is stored as a 19-character
-- 'YYYY-MM-DD HH:MM:SS' string, so string order equals chronological order and the
-- index on Time is usable. Wrapping it in datetime() defeats both.
--
-- BEFORE RUNNING
--   1. Stop the container.
--   2. Back up:  sqlite3 Sessy.db "VACUUM INTO 'Sessy-before-gapfill.db';"
--   3. Dry run: change the final COMMIT to ROLLBACK, inspect the verification output.
--   4. Real run: COMMIT.
--
--   sqlite3 Sessy.db < RepairEnergyHistoryGap.sql
-- ─────────────────────────────────────────────────────────────────────────────

-- Nothing below is CLI-specific, so this file also runs in DB Browser, DBeaver or any
-- other client. For readable output in the sqlite3 shell, type these two dot-commands
-- yourself before running it — they are shell commands, not SQL, and a client that
-- sends them to SQLite reports: near ".": syntax error.
--     .headers on
--     .mode column

BEGIN TRANSACTION;

-- ── Parameters ───────────────────────────────────────────────────────────────
-- gap_end      : timestamp of the first GOOD reading after the gap.
-- profile_days : how many days before the gap are used to build the shape.
-- max_slots    : safety cap on the number of reconstructed quarters.
DROP TABLE IF EXISTS temp.p;
CREATE TEMP TABLE p AS
SELECT '2026-07-29 10:30:00' AS gap_end,
       14                    AS profile_days,
       2000                  AS max_slots;

-- ── Anchors: last row before the gap, first row after it (per meter) ─────────
DROP TABLE IF EXISTS temp.bounds;
CREATE TEMP TABLE bounds AS
WITH endrow AS (
    SELECT MeterId, MIN(Time) AS end_time
    FROM EnergyHistory
    WHERE Time >= (SELECT gap_end FROM p)
    GROUP BY MeterId
),
anchor AS (
    SELECT er.MeterId,
           er.end_time,
           (SELECT MAX(s.Time)
            FROM EnergyHistory s
            WHERE s.MeterId IS er.MeterId
              AND s.Time < er.end_time) AS start_time
    FROM endrow er
)
SELECT a.MeterId,
       a.start_time,
       a.end_time,
       s.ConsumedTariff1 AS c1_0, s.ConsumedTariff2 AS c2_0,
       s.ProducedTariff1 AS p1_0, s.ProducedTariff2 AS p2_0,
       e.ConsumedTariff1 AS c1_1, e.ConsumedTariff2 AS c2_1,
       e.ProducedTariff1 AS p1_1, e.ProducedTariff2 AS p2_1
FROM anchor a
JOIN EnergyHistory s ON s.MeterId IS a.MeterId AND s.Time = a.start_time
JOIN EnergyHistory e ON e.MeterId IS a.MeterId AND e.Time = a.end_time;

SELECT '=== ANCHORS ===' AS check_1;
SELECT MeterId, start_time, end_time,
       CAST(ROUND((julianday(end_time) - julianday(start_time)) * 96) AS INT) - 1 AS missing_slots,
       ROUND(c1_1 - c1_0, 1) AS delta_consumed_t1,
       ROUND(c2_1 - c2_0, 1) AS delta_consumed_t2,
       ROUND(p1_1 - p1_0, 1) AS delta_produced_t1,
       ROUND(p2_1 - p2_0, 1) AS delta_produced_t2
FROM bounds;

-- ── Missing 15-minute slots ─────────────────────────────────────────────────
-- A bare counter drives the recursion; joining bounds inside a recursive step makes
-- SQLite re-evaluate it per iteration.
--
-- The closing quarter (end_time itself) is included here on purpose: it carries a
-- share of the shape, so the measured delta is spread over every interval including
-- the last one. gap_rows drops it again — that row already exists.
DROP TABLE IF EXISTS temp.slots;
CREATE TEMP TABLE slots AS
WITH RECURSIVE n(i) AS (
    SELECT 1
    UNION ALL
    SELECT i + 1 FROM n WHERE i < (SELECT max_slots FROM p)
)
SELECT b.MeterId,
       datetime(b.start_time, '+' || (n.i * 15) || ' minutes') AS t
FROM bounds b
JOIN n
WHERE datetime(b.start_time, '+' || (n.i * 15) || ' minutes') <= b.end_time;

-- ── Shape: average per-quarter increments per time-of-day, before the gap ────
-- Only consecutive rows exactly 15 minutes apart are used; negative increments
-- (meter swap / corrupt row) are clamped to 0.
DROP TABLE IF EXISTS temp.prof;
CREATE TEMP TABLE prof AS
WITH src AS (
    SELECT MeterId,
           Time AS t,
           CASE WHEN strftime('%w', Time) IN ('0','6') THEN 1 ELSE 0 END AS is_we,
           substr(Time, 12, 5) AS tod,
           TarrifIndicator,
           Temperature,
           ConsumedTariff1, ConsumedTariff2, ProducedTariff1, ProducedTariff2,
           LAG(Time)            OVER (PARTITION BY MeterId ORDER BY Time) AS pt,
           LAG(ConsumedTariff1) OVER (PARTITION BY MeterId ORDER BY Time) AS pc1,
           LAG(ConsumedTariff2) OVER (PARTITION BY MeterId ORDER BY Time) AS pc2,
           LAG(ProducedTariff1) OVER (PARTITION BY MeterId ORDER BY Time) AS pp1,
           LAG(ProducedTariff2) OVER (PARTITION BY MeterId ORDER BY Time) AS pp2
    FROM EnergyHistory
    WHERE Time >  (SELECT datetime(MIN(start_time), '-' || (SELECT profile_days FROM p) || ' days') FROM bounds)
      AND Time <= (SELECT MIN(start_time) FROM bounds)
)
SELECT MeterId, is_we, tod,
       AVG(MAX(ConsumedTariff1 - pc1, 0)) AS w_c1,
       AVG(MAX(ConsumedTariff2 - pc2, 0)) AS w_c2,
       AVG(MAX(ProducedTariff1 - pp1, 0)) AS w_p1,
       AVG(MAX(ProducedTariff2 - pp2, 0)) AS w_p2,
       AVG(NULLIF(Temperature, -999))     AS avg_temp,
       MIN(TarrifIndicator)               AS tariff
FROM src
WHERE pt IS NOT NULL
  AND ROUND((julianday(t) - julianday(pt)) * 1440) = 15
GROUP BY MeterId, is_we, tod;

SELECT '=== PROFILE ===' AS check_2;
SELECT MeterId, is_we, COUNT(*) AS slots_of_day,
       ROUND(SUM(w_c1) + SUM(w_c2), 1) AS avg_day_consumed,
       ROUND(SUM(w_p1) + SUM(w_p2), 1) AS avg_day_produced
FROM prof GROUP BY MeterId, is_we;

-- ── Weights per missing slot, plus running and grand totals ─────────────────
DROP TABLE IF EXISTS temp.weighted;
CREATE TEMP TABLE weighted AS
WITH w AS (
    SELECT s.MeterId,
           s.t,
           COALESCE(pr.w_c1, 0) AS w_c1,
           COALESCE(pr.w_c2, 0) AS w_c2,
           COALESCE(pr.w_p1, 0) AS w_p1,
           COALESCE(pr.w_p2, 0) AS w_p2,
           pr.avg_temp,
           pr.tariff
    FROM slots s
    LEFT JOIN prof pr
           ON pr.MeterId IS s.MeterId
          AND pr.tod    =  substr(s.t, 12, 5)
          AND pr.is_we  =  CASE WHEN strftime('%w', s.t) IN ('0','6') THEN 1 ELSE 0 END
)
SELECT MeterId, t, avg_temp, tariff,
       SUM(w_c1) OVER (PARTITION BY MeterId ORDER BY t) AS cum_c1,
       SUM(w_c2) OVER (PARTITION BY MeterId ORDER BY t) AS cum_c2,
       SUM(w_p1) OVER (PARTITION BY MeterId ORDER BY t) AS cum_p1,
       SUM(w_p2) OVER (PARTITION BY MeterId ORDER BY t) AS cum_p2,
       SUM(w_c1) OVER (PARTITION BY MeterId)            AS tot_c1,
       SUM(w_c2) OVER (PARTITION BY MeterId)            AS tot_c2,
       SUM(w_p1) OVER (PARTITION BY MeterId)            AS tot_p1,
       SUM(w_p2) OVER (PARTITION BY MeterId)            AS tot_p2,
       ROW_NUMBER() OVER (PARTITION BY MeterId ORDER BY t) AS rn,
       COUNT(*)     OVER (PARTITION BY MeterId)            AS n
FROM w;

-- ── Reconstructed rows ──────────────────────────────────────────────────────
-- Counter = start + measured_delta * (share of the shape up to this slot).
-- If the shape is flat zero for a counter (e.g. no production at all in the
-- reference days) the delta is spread linearly instead.
DROP TABLE IF EXISTS temp.gap_rows;
CREATE TEMP TABLE gap_rows AS
SELECT
    w.MeterId,
    w.t AS Time,
    ROUND(b.c1_0 + (b.c1_1 - b.c1_0) *
        CASE WHEN w.tot_c1 > 0 THEN w.cum_c1 / w.tot_c1 ELSE CAST(w.rn AS REAL) / w.n END, 3) AS ConsumedTariff1,
    ROUND(b.c2_0 + (b.c2_1 - b.c2_0) *
        CASE WHEN w.tot_c2 > 0 THEN w.cum_c2 / w.tot_c2 ELSE CAST(w.rn AS REAL) / w.n END, 3) AS ConsumedTariff2,
    ROUND(b.p1_0 + (b.p1_1 - b.p1_0) *
        CASE WHEN w.tot_p1 > 0 THEN w.cum_p1 / w.tot_p1 ELSE CAST(w.rn AS REAL) / w.n END, 3) AS ProducedTariff1,
    ROUND(b.p2_0 + (b.p2_1 - b.p2_0) *
        CASE WHEN w.tot_p2 > 0 THEN w.cum_p2 / w.tot_p2 ELSE CAST(w.rn AS REAL) / w.n END, 3) AS ProducedTariff2,
    -- Tariff indicator: prefer the row exactly one week earlier (same weekday and time).
    COALESCE(h.TarrifIndicator, w.tariff, 0) AS TarrifIndicator,
    -- Temperature: the live weather stored by ConsumptionMonitorService for that quarter
    -- (that service kept running), otherwise the time-of-day average.
    ROUND(COALESCE(NULLIF(c.Temperature, -999), w.avg_temp, 0), 1) AS Temperature
FROM weighted w
JOIN bounds b ON b.MeterId IS w.MeterId
LEFT JOIN EnergyHistory h
       ON h.MeterId IS w.MeterId
      AND h.Time = datetime(w.t, '-7 days')
LEFT JOIN Consumption c
       ON substr(c.Time, 1, 19) = w.t
WHERE w.t < b.end_time;

-- ── Verification (inspect before committing) ────────────────────────────────
SELECT '=== ROWS TO INSERT ===' AS check_3;
SELECT MeterId, COUNT(*) AS rows_to_insert, MIN(Time) AS first, MAX(Time) AS last
FROM gap_rows GROUP BY MeterId;

SELECT '=== NO OVERLAP WITH EXISTING ROWS (must be 0) ===' AS check_4;
SELECT COUNT(*) AS collisions
FROM gap_rows g
JOIN EnergyHistory e ON e.MeterId IS g.MeterId AND e.Time = g.Time;

SELECT '=== MONOTONICITY OF THE FILLED ROWS (must all be 0) ===' AS check_5;
WITH chk AS (
    SELECT ConsumedTariff1, ConsumedTariff2, ProducedTariff1, ProducedTariff2,
           LAG(ConsumedTariff1) OVER (PARTITION BY MeterId ORDER BY Time) AS l1,
           LAG(ConsumedTariff2) OVER (PARTITION BY MeterId ORDER BY Time) AS l2,
           LAG(ProducedTariff1) OVER (PARTITION BY MeterId ORDER BY Time) AS l3,
           LAG(ProducedTariff2) OVER (PARTITION BY MeterId ORDER BY Time) AS l4
    FROM gap_rows
)
SELECT SUM(ConsumedTariff1 < l1) AS drops_c1,
       SUM(ConsumedTariff2 < l2) AS drops_c2,
       SUM(ProducedTariff1 < l3) AS drops_p1,
       SUM(ProducedTariff2 < l4) AS drops_p2
FROM chk WHERE l1 IS NOT NULL;

SELECT '=== JOIN ONTO THE CLOSING ANCHOR ===' AS check_6;
-- Increment of the existing anchor row over the last reconstructed one.
-- Must be >= 0 and of the size of a single quarter.
SELECT b.MeterId,
       ROUND(b.c1_1 - (SELECT ConsumedTariff1 FROM gap_rows g WHERE g.MeterId IS b.MeterId ORDER BY g.Time DESC LIMIT 1), 3) AS last_step_c1,
       ROUND(b.c2_1 - (SELECT ConsumedTariff2 FROM gap_rows g WHERE g.MeterId IS b.MeterId ORDER BY g.Time DESC LIMIT 1), 3) AS last_step_c2,
       ROUND(b.p1_1 - (SELECT ProducedTariff1 FROM gap_rows g WHERE g.MeterId IS b.MeterId ORDER BY g.Time DESC LIMIT 1), 3) AS last_step_p1,
       ROUND(b.p2_1 - (SELECT ProducedTariff2 FROM gap_rows g WHERE g.MeterId IS b.MeterId ORDER BY g.Time DESC LIMIT 1), 3) AS last_step_p2
FROM bounds b;

SELECT '=== BOUNDARY STEPS (opening anchor → first row, last row → closing anchor) ===' AS check_7;
SELECT 'open' AS edge,
       ROUND((SELECT MIN(ConsumedTariff2) FROM gap_rows) - b.c2_0, 3) AS step_c2,
       ROUND((SELECT MIN(ProducedTariff2) FROM gap_rows) - b.p2_0, 3) AS step_p2
FROM bounds b
UNION ALL
SELECT 'close',
       ROUND(b.c2_1 - (SELECT MAX(ConsumedTariff2) FROM gap_rows), 3),
       ROUND(b.p2_1 - (SELECT MAX(ProducedTariff2) FROM gap_rows), 3)
FROM bounds b;

SELECT '=== LARGEST QUARTER INCREMENT vs REFERENCE DAYS ===' AS check_8;
WITH d AS (
    SELECT ConsumedTariff2 - LAG(ConsumedTariff2) OVER (ORDER BY Time) AS dc2,
           ProducedTariff2 - LAG(ProducedTariff2) OVER (ORDER BY Time) AS dp2
    FROM gap_rows
)
SELECT ROUND(MAX(dc2), 1) AS max_quarter_consumed,
       ROUND(MAX(dp2), 1) AS max_quarter_produced,
       (SELECT ROUND(MAX(w_c2), 1) FROM prof) AS reference_max_consumed,
       (SELECT ROUND(MAX(w_p2), 1) FROM prof) AS reference_max_produced
FROM d;

SELECT '=== SAMPLE (every 8th row) ===' AS check_9;
SELECT Time, ConsumedTariff1, ConsumedTariff2, ProducedTariff1, ProducedTariff2, TarrifIndicator, Temperature
FROM (SELECT *, ROW_NUMBER() OVER (ORDER BY Time) AS rn FROM gap_rows)
WHERE rn % 8 = 1;

-- ── Insert ──────────────────────────────────────────────────────────────────
INSERT INTO EnergyHistory
    (Time, MeterId, ConsumedTariff1, ConsumedTariff2, ProducedTariff1, ProducedTariff2, TarrifIndicator, Temperature)
SELECT Time, MeterId, ConsumedTariff1, ConsumedTariff2, ProducedTariff1, ProducedTariff2, TarrifIndicator, Temperature
FROM gap_rows
WHERE NOT EXISTS (
    SELECT 1 FROM EnergyHistory e
    WHERE e.MeterId IS gap_rows.MeterId AND e.Time = gap_rows.Time
);

SELECT '=== INSERTED ===' AS result, changes() AS rows_inserted;

-- Dry run: replace COMMIT with ROLLBACK.
COMMIT;

-- ── Undo (only valid before the app writes new EnergyHistory rows) ──────────
-- DELETE FROM EnergyHistory
--  WHERE Time > '2026-07-27 22:45:00' AND Time < '2026-07-29 10:30:00';
