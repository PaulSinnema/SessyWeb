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
--       measured values of the closing anchor row (today 10:30).
--   So totals over the gap are exact; the distribution inside the gap is an estimate.
--
-- THESE ROWS ARE RECONSTRUCTED, NOT MEASURED. They feed EnergyStatisticsService and
-- FinancialResultsService, so per-quarter figures in that window are approximations.
--
-- BEFORE RUNNING
--   1. Stop the container.
--   2. Back up:  sqlite3 Sessy.db "VACUUM INTO 'Sessy-before-gapfill.db';"
--   3. Dry run: change the final COMMIT to ROLLBACK, inspect the verification output.
--   4. Real run: COMMIT.
--
--   sqlite3 Sessy.db < RepairEnergyHistoryGap.sql
-- ─────────────────────────────────────────────────────────────────────────────

.headers on
.mode column

BEGIN TRANSACTION;

-- ── Parameters ───────────────────────────────────────────────────────────────
-- gap_end      : timestamp of the first GOOD reading after the gap.
-- profile_days : how many days before the gap are used to build the shape.
DROP TABLE IF EXISTS temp.p;
CREATE TEMP TABLE p AS
SELECT '2026-07-29 10:30:00' AS gap_end,
       14                    AS profile_days;

-- ── Anchors: last row before the gap, first row after it (per meter) ─────────
DROP TABLE IF EXISTS temp.bounds;
CREATE TEMP TABLE bounds AS
WITH endrow AS (
    SELECT MeterId, MIN(datetime(Time)) AS end_time
    FROM EnergyHistory
    WHERE datetime(Time) >= datetime((SELECT gap_end FROM p))
    GROUP BY MeterId
),
anchor AS (
    SELECT er.MeterId,
           er.end_time,
           (SELECT MAX(datetime(s.Time))
            FROM EnergyHistory s
            WHERE s.MeterId IS er.MeterId
              AND datetime(s.Time) < er.end_time) AS start_time
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
JOIN EnergyHistory s ON s.MeterId IS a.MeterId AND datetime(s.Time) = a.start_time
JOIN EnergyHistory e ON e.MeterId IS a.MeterId AND datetime(e.Time) = a.end_time;

SELECT '=== ANCHORS ===' AS check_1;
SELECT MeterId, start_time, end_time,
       ROUND((julianday(end_time) - julianday(start_time)) * 96) - 1 AS missing_slots,
       ROUND(c1_1 - c1_0, 1) AS delta_consumed_t1,
       ROUND(c2_1 - c2_0, 1) AS delta_consumed_t2,
       ROUND(p1_1 - p1_0, 1) AS delta_produced_t1,
       ROUND(p2_1 - p2_0, 1) AS delta_produced_t2
FROM bounds;

-- ── Missing 15-minute slots ─────────────────────────────────────────────────
DROP TABLE IF EXISTS temp.slots;
CREATE TEMP TABLE slots AS
WITH RECURSIVE seq(MeterId, t) AS (
    SELECT MeterId, datetime(start_time, '+15 minutes') FROM bounds
    UNION ALL
    SELECT s.MeterId, datetime(s.t, '+15 minutes')
    FROM seq s
    JOIN bounds b ON b.MeterId IS s.MeterId
    WHERE datetime(s.t, '+15 minutes') < b.end_time
)
SELECT MeterId, t FROM seq;

-- ── Shape: average per-quarter increments per time-of-day, before the gap ────
-- Only consecutive rows exactly 15 minutes apart are used; negative increments
-- (meter swap / corrupt row) are clamped to 0.
DROP TABLE IF EXISTS temp.prof;
CREATE TEMP TABLE prof AS
WITH src AS (
    SELECT MeterId,
           datetime(Time) AS t,
           CASE WHEN strftime('%w', Time) IN ('0','6') THEN 1 ELSE 0 END AS is_we,
           strftime('%H:%M', Time) AS tod,
           TarrifIndicator,
           Temperature,
           ConsumedTariff1, ConsumedTariff2, ProducedTariff1, ProducedTariff2,
           LAG(datetime(Time))    OVER (PARTITION BY MeterId ORDER BY Time) AS pt,
           LAG(ConsumedTariff1)   OVER (PARTITION BY MeterId ORDER BY Time) AS pc1,
           LAG(ConsumedTariff2)   OVER (PARTITION BY MeterId ORDER BY Time) AS pc2,
           LAG(ProducedTariff1)   OVER (PARTITION BY MeterId ORDER BY Time) AS pp1,
           LAG(ProducedTariff2)   OVER (PARTITION BY MeterId ORDER BY Time) AS pp2
    FROM EnergyHistory
    WHERE datetime(Time) >  datetime((SELECT MIN(start_time) FROM bounds),
                                     '-' || (SELECT profile_days FROM p) || ' days')
      AND datetime(Time) <= (SELECT MIN(start_time) FROM bounds)
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
          AND pr.tod    =  strftime('%H:%M', s.t)
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
    -- Match the timestamp format already used in the table.
    CASE WHEN (SELECT instr(Time, '.') FROM EnergyHistory ORDER BY Id DESC LIMIT 1) > 0
         THEN strftime('%Y-%m-%d %H:%M:%S.0000000', w.t)
         ELSE strftime('%Y-%m-%d %H:%M:%S', w.t)
    END AS Time,
    ROUND(b.c1_0 + (b.c1_1 - b.c1_0) *
        CASE WHEN w.tot_c1 > 0 THEN w.cum_c1 / w.tot_c1 ELSE CAST(w.rn AS REAL) / w.n END, 3) AS ConsumedTariff1,
    ROUND(b.c2_0 + (b.c2_1 - b.c2_0) *
        CASE WHEN w.tot_c2 > 0 THEN w.cum_c2 / w.tot_c2 ELSE CAST(w.rn AS REAL) / w.n END, 3) AS ConsumedTariff2,
    ROUND(b.p1_0 + (b.p1_1 - b.p1_0) *
        CASE WHEN w.tot_p1 > 0 THEN w.cum_p1 / w.tot_p1 ELSE CAST(w.rn AS REAL) / w.n END, 3) AS ProducedTariff1,
    ROUND(b.p2_0 + (b.p2_1 - b.p2_0) *
        CASE WHEN w.tot_p2 > 0 THEN w.cum_p2 / w.tot_p2 ELSE CAST(w.rn AS REAL) / w.n END, 3) AS ProducedTariff2,
    -- Tariff indicator: prefer the row exactly one week earlier (same weekday/time).
    COALESCE(
        (SELECT h.TarrifIndicator FROM EnergyHistory h
          WHERE h.MeterId IS w.MeterId AND datetime(h.Time) = datetime(w.t, '-7 days') LIMIT 1),
        w.tariff,
        0) AS TarrifIndicator,
    -- Temperature: the live weather stored by ConsumptionMonitorService for that
    -- quarter (that service kept running), otherwise the time-of-day average.
    ROUND(COALESCE(
        (SELECT NULLIF(c.Temperature, -999) FROM Consumption c
          WHERE datetime(c.Time) = w.t LIMIT 1),
        w.avg_temp,
        0), 1) AS Temperature
FROM weighted w
JOIN bounds b ON b.MeterId IS w.MeterId;

-- ── Verification (inspect before committing) ────────────────────────────────
SELECT '=== ROWS TO INSERT ===' AS check_3;
SELECT MeterId, COUNT(*) AS rows_to_insert, MIN(Time) AS first, MAX(Time) AS last
FROM gap_rows GROUP BY MeterId;

SELECT '=== NO OVERLAP WITH EXISTING ROWS (must be 0) ===' AS check_4;
SELECT COUNT(*) AS collisions
FROM gap_rows g
JOIN EnergyHistory e ON e.MeterId IS g.MeterId AND datetime(e.Time) = datetime(g.Time);

SELECT '=== MONOTONICITY (must all be 0) ===' AS check_5;
WITH chk AS (
    SELECT MeterId, Time, ConsumedTariff1, ConsumedTariff2, ProducedTariff1, ProducedTariff2,
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

SELECT '=== JOIN ON THE CLOSING ANCHOR (last generated value vs measured) ===' AS check_6;
SELECT b.MeterId,
       ROUND(b.c1_1 - (SELECT ConsumedTariff1 FROM gap_rows g WHERE g.MeterId IS b.MeterId ORDER BY g.Time DESC LIMIT 1), 3) AS remainder_c1,
       ROUND(b.c2_1 - (SELECT ConsumedTariff2 FROM gap_rows g WHERE g.MeterId IS b.MeterId ORDER BY g.Time DESC LIMIT 1), 3) AS remainder_c2,
       ROUND(b.p1_1 - (SELECT ProducedTariff1 FROM gap_rows g WHERE g.MeterId IS b.MeterId ORDER BY g.Time DESC LIMIT 1), 3) AS remainder_p1,
       ROUND(b.p2_1 - (SELECT ProducedTariff2 FROM gap_rows g WHERE g.MeterId IS b.MeterId ORDER BY g.Time DESC LIMIT 1), 3) AS remainder_p2
FROM bounds b;
-- remainder_* is the increment of the final (existing) 10:30 row — must be small and >= 0.

SELECT '=== SAMPLE (every 8th row) ===' AS check_7;
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
    WHERE e.MeterId IS gap_rows.MeterId AND datetime(e.Time) = datetime(gap_rows.Time)
);

SELECT '=== INSERTED ===' AS result, changes() AS rows_inserted;

-- Dry run: replace COMMIT with ROLLBACK.
COMMIT;

-- ── Undo (only valid before the app writes new EnergyHistory rows) ──────────
-- DELETE FROM EnergyHistory
--  WHERE datetime(Time) > '2026-07-27 22:15:00' AND datetime(Time) < '2026-07-29 10:30:00';
