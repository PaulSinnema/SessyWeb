-- ─────────────────────────────────────────────────────────────────────────────
-- RepairEnergyHistoryGap.Console.sql
--
-- Version for the SQL Console in SessyWeb => Settings. That console sends the whole
-- text as ONE command, so this file is a single INSERT statement:
--   * no .headers / .mode  — those are sqlite3-shell commands, not SQL, and the
--     console reports them as: near ".": syntax error
--   * no temp tables and no BEGIN/COMMIT — everything is CTEs inside the statement
--
-- Paste this whole file into the console and run it once. It reports
-- "142 row(s) affected". Running it a second time affects 0 rows: the NOT EXISTS
-- guard at the bottom skips timestamps that are already present.
--
-- Verify afterwards with the SELECTs at the bottom of this file — paste them ONE AT
-- A TIME and make sure the text you paste STARTS with SELECT. The console decides
-- between "show a grid" and "execute" by looking at the first word, so a leading
-- comment line turns a SELECT into a silent no-op reporting "0 row(s) affected".
--
-- Back up first: Settings has a database backup button, or copy Sessy.db while the
-- container is stopped. Undo is at the very bottom.
--
-- THESE ROWS ARE RECONSTRUCTED, NOT MEASURED. The totals over the gap are exact —
-- they are scaled onto the measured anchor readings — but the distribution within
-- the gap is an estimate built from the same time-of-day on the preceding days.
-- ─────────────────────────────────────────────────────────────────────────────

WITH RECURSIVE
-- Closing anchor: first stored reading at or after the gap (per meter).
endrow AS (
    SELECT MeterId, MIN(Time) AS end_time
      FROM EnergyHistory
     WHERE Time >= '2026-07-29 10:30:00'
     GROUP BY MeterId
),
-- Opening anchor: last reading before that.
anchor AS (
    SELECT er.MeterId,
           er.end_time,
           (SELECT MAX(s.Time)
              FROM EnergyHistory s
             WHERE s.MeterId IS er.MeterId
               AND s.Time < er.end_time) AS start_time
      FROM endrow er
),
bounds AS (
    SELECT a.MeterId, a.start_time, a.end_time,
           s.ConsumedTariff1 AS c1_0, s.ConsumedTariff2 AS c2_0,
           s.ProducedTariff1 AS p1_0, s.ProducedTariff2 AS p2_0,
           e.ConsumedTariff1 AS c1_1, e.ConsumedTariff2 AS c2_1,
           e.ProducedTariff1 AS p1_1, e.ProducedTariff2 AS p2_1
      FROM anchor a
      JOIN EnergyHistory s ON s.MeterId IS a.MeterId AND s.Time = a.start_time
      JOIN EnergyHistory e ON e.MeterId IS a.MeterId AND e.Time = a.end_time
),
-- Bare counter: joining bounds inside a recursive step makes SQLite re-evaluate it
-- on every iteration.
n(i) AS (
    SELECT 1
    UNION ALL
    SELECT i + 1 FROM n WHERE i < 2000
),
-- The closing quarter (end_time itself) is included on purpose: it carries a share of
-- the shape, so the measured delta is spread over every interval including the last.
-- gap_rows drops it again — that row already exists.
slots AS (
    SELECT b.MeterId,
           datetime(b.start_time, '+' || (n.i * 15) || ' minutes') AS t
      FROM bounds b
      JOIN n
     WHERE datetime(b.start_time, '+' || (n.i * 15) || ' minutes') <= b.end_time
),
-- Reference days: consecutive readings before the gap, with their increments.
src AS (
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
     WHERE Time >  (SELECT datetime(MIN(start_time), '-14 days') FROM bounds)
       AND Time <= (SELECT MIN(start_time) FROM bounds)
),
-- Average increment per time-of-day. Only pairs exactly 15 minutes apart count;
-- negative increments (meter swap / corrupt row) are clamped to 0.
prof AS (
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
     GROUP BY MeterId, is_we, tod
),
-- Attach the shape to each missing quarter.
shaped AS (
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
),
-- Running share of the shape, plus the totals used to normalise it.
weighted AS (
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
      FROM shaped
),
-- Counter = opening value + measured delta * share of the shape so far.
-- A flat-zero shape (e.g. no export at all in the reference days) falls back to
-- spreading the delta linearly.
gap_rows AS (
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
        -- Temperature: live weather stored by ConsumptionMonitorService for that quarter
        -- (that service kept running), otherwise the time-of-day average.
        ROUND(COALESCE(NULLIF(c.Temperature, -999), w.avg_temp, 0), 1) AS Temperature
      FROM weighted w
      JOIN bounds b ON b.MeterId IS w.MeterId
      LEFT JOIN EnergyHistory h
             ON h.MeterId IS w.MeterId
            AND h.Time = datetime(w.t, '-7 days')
      LEFT JOIN Consumption c
             ON substr(c.Time, 1, 19) = w.t
     WHERE w.t < b.end_time
)
INSERT INTO EnergyHistory
    (Time, MeterId, ConsumedTariff1, ConsumedTariff2, ProducedTariff1, ProducedTariff2, TarrifIndicator, Temperature)
SELECT Time, MeterId, ConsumedTariff1, ConsumedTariff2, ProducedTariff1, ProducedTariff2, TarrifIndicator, Temperature
  FROM gap_rows
 WHERE NOT EXISTS (
        SELECT 1 FROM EnergyHistory e
         WHERE e.MeterId IS gap_rows.MeterId
           AND e.Time = gap_rows.Time);


-- ─────────────────────────────────────────────────────────────────────────────
-- VERIFICATION — paste one at a time, each must start with SELECT.
-- ─────────────────────────────────────────────────────────────────────────────

-- 1. How many rows are there now, and where do they start and end?
-- SELECT COUNT(*) AS filled, MIN(Time) AS first, MAX(Time) AS last
--   FROM EnergyHistory
--  WHERE Time > '2026-07-27 22:45:00' AND Time < '2026-07-29 10:30:00';

-- 2. Continuity: every gap 15 minutes, no counter ever decreasing. All three 0.
-- SELECT SUM(gap_min <> 15) AS bad_gaps, SUM(dc2 < 0) AS negative_consumed, SUM(dp2 < 0) AS negative_produced
--   FROM (SELECT ROUND((julianday(Time) - julianday(LAG(Time) OVER (ORDER BY Time))) * 1440) AS gap_min,
--                ConsumedTariff2 - LAG(ConsumedTariff2) OVER (ORDER BY Time) AS dc2,
--                ProducedTariff2 - LAG(ProducedTariff2) OVER (ORDER BY Time) AS dp2
--           FROM EnergyHistory
--          WHERE Time >= '2026-07-27 20:00:00' AND Time <= '2026-07-29 11:00:00')
--  WHERE dc2 IS NOT NULL;

-- 3. The two seams: last measured row before, first reconstructed row after, and vice versa.
-- SELECT Time, ConsumedTariff2, ProducedTariff2 FROM EnergyHistory
--  WHERE Time BETWEEN '2026-07-27 22:30:00' AND '2026-07-27 23:30:00'
--     OR Time BETWEEN '2026-07-29 10:00:00' AND '2026-07-29 10:45:00'
--  ORDER BY Time;

-- ─────────────────────────────────────────────────────────────────────────────
-- UNDO — only valid before the app has written new rows in this window.
-- ─────────────────────────────────────────────────────────────────────────────
-- DELETE FROM EnergyHistory
--  WHERE Time > '2026-07-27 22:45:00' AND Time < '2026-07-29 10:30:00';
