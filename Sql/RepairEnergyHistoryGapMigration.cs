// ─────────────────────────────────────────────────────────────────────────────
// Paste-ready migration body for the EnergyHistory gap repair.
//
// This file is NOT compiled (it lives outside every csproj). Generate an empty
// migration and paste Up()/Down() into it:
//
//   dotnet build C:\Projects\Sessy\SessyController.sln
//   dotnet ef migrations add RepairEnergyHistoryGap --project SessyData --startup-project SessyWeb
//
// It runs inside the migration transaction from Program.cs (dbContext.Database.Migrate()),
// which is preceded by the automatic VACUUM INTO backup.
//
// WHAT IT DOES
//   For every 15-minute slot between the last reading before the gap and the first
//   reading after it, a row is reconstructed:
//     * the SHAPE (consumption/production per quarter) comes from the same time-of-day
//       in the days before the gap, matched on weekday vs weekend;
//     * the shape is SCALED so the cumulative counters land exactly on the measured
//       values of the closing anchor row (GapEnd).
//   Totals over the gap are therefore exact; the distribution inside it is an estimate.
//   THESE ROWS ARE RECONSTRUCTED, NOT MEASURED — EnergyStatisticsService and
//   FinancialResultsService will report approximations for that window.
//
//   Safe on an empty or already-repaired database: without an anchor row at/after
//   GapEnd nothing is selected, and existing timestamps are skipped.
// ─────────────────────────────────────────────────────────────────────────────

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SessyData.Migrations
{
    /// <inheritdoc />
    public partial class RepairEnergyHistoryGap : Migration
    {
        // First good reading after the gap; the reconstruction is scaled to join onto it.
        private const string GapEnd = "2026-07-29 10:30:00";

        // Days before the gap used to build the time-of-day shape.
        private const int ProfileDays = 14;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
WITH RECURSIVE
-- Closing anchor: first stored reading at or after the gap (per meter).
endrow AS (
    SELECT MeterId, MIN(datetime(Time)) AS end_time
      FROM EnergyHistory
     WHERE datetime(Time) >= datetime('{GapEnd}')
     GROUP BY MeterId
),
-- Opening anchor: last reading before that.
anchor AS (
    SELECT er.MeterId,
           er.end_time,
           (SELECT MAX(datetime(s.Time))
              FROM EnergyHistory s
             WHERE s.MeterId IS er.MeterId
               AND datetime(s.Time) < er.end_time) AS start_time
      FROM endrow er
),
bounds AS (
    SELECT a.MeterId, a.start_time, a.end_time,
           s.ConsumedTariff1 AS c1_0, s.ConsumedTariff2 AS c2_0,
           s.ProducedTariff1 AS p1_0, s.ProducedTariff2 AS p2_0,
           e.ConsumedTariff1 AS c1_1, e.ConsumedTariff2 AS c2_1,
           e.ProducedTariff1 AS p1_1, e.ProducedTariff2 AS p2_1
      FROM anchor a
      JOIN EnergyHistory s ON s.MeterId IS a.MeterId AND datetime(s.Time) = a.start_time
      JOIN EnergyHistory e ON e.MeterId IS a.MeterId AND datetime(e.Time) = a.end_time
),
-- Every missing quarter between the two anchors.
seq(MeterId, t) AS (
    SELECT MeterId, datetime(start_time, '+15 minutes')
      FROM bounds
     WHERE datetime(start_time, '+15 minutes') < end_time
    UNION ALL
    SELECT q.MeterId, datetime(q.t, '+15 minutes')
      FROM seq q
      JOIN bounds b ON b.MeterId IS q.MeterId
     WHERE datetime(q.t, '+15 minutes') < b.end_time
),
-- Reference days: consecutive readings before the gap, with their increments.
src AS (
    SELECT MeterId,
           datetime(Time) AS t,
           CASE WHEN strftime('%w', Time) IN ('0','6') THEN 1 ELSE 0 END AS is_we,
           strftime('%H:%M', Time) AS tod,
           TarrifIndicator,
           Temperature,
           ConsumedTariff1, ConsumedTariff2, ProducedTariff1, ProducedTariff2,
           LAG(datetime(Time))  OVER (PARTITION BY MeterId ORDER BY Time) AS pt,
           LAG(ConsumedTariff1) OVER (PARTITION BY MeterId ORDER BY Time) AS pc1,
           LAG(ConsumedTariff2) OVER (PARTITION BY MeterId ORDER BY Time) AS pc2,
           LAG(ProducedTariff1) OVER (PARTITION BY MeterId ORDER BY Time) AS pp1,
           LAG(ProducedTariff2) OVER (PARTITION BY MeterId ORDER BY Time) AS pp2
      FROM EnergyHistory
     WHERE datetime(Time) >  datetime((SELECT MIN(start_time) FROM bounds), '-{ProfileDays} days')
       AND datetime(Time) <= (SELECT MIN(start_time) FROM bounds)
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
    SELECT q.MeterId,
           q.t,
           COALESCE(pr.w_c1, 0) AS w_c1,
           COALESCE(pr.w_c2, 0) AS w_c2,
           COALESCE(pr.w_p1, 0) AS w_p1,
           COALESCE(pr.w_p2, 0) AS w_p2,
           pr.avg_temp,
           pr.tariff
      FROM seq q
      LEFT JOIN prof pr
             ON pr.MeterId IS q.MeterId
            AND pr.tod    =  strftime('%H:%M', q.t)
            AND pr.is_we  =  CASE WHEN strftime('%w', q.t) IN ('0','6') THEN 1 ELSE 0 END
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
-- A flat-zero shape (e.g. no production at all in the reference days) falls back
-- to spreading the delta linearly.
gap_rows AS (
    SELECT
        w.MeterId,
        -- Match the timestamp format already present in the table.
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
        -- Tariff indicator: prefer the row exactly one week earlier (same weekday and time).
        COALESCE(
            (SELECT h.TarrifIndicator FROM EnergyHistory h
              WHERE h.MeterId IS w.MeterId AND datetime(h.Time) = datetime(w.t, '-7 days') LIMIT 1),
            w.tariff,
            0) AS TarrifIndicator,
        -- Temperature: live weather stored by ConsumptionMonitorService for that quarter
        -- (that service kept running), otherwise the time-of-day average.
        ROUND(COALESCE(
            (SELECT NULLIF(c.Temperature, -999) FROM Consumption c
              WHERE datetime(c.Time) = w.t LIMIT 1),
            w.avg_temp,
            0), 1) AS Temperature
      FROM weighted w
      JOIN bounds b ON b.MeterId IS w.MeterId
)
INSERT INTO EnergyHistory
    (Time, MeterId, ConsumedTariff1, ConsumedTariff2, ProducedTariff1, ProducedTariff2, TarrifIndicator, Temperature)
SELECT Time, MeterId, ConsumedTariff1, ConsumedTariff2, ProducedTariff1, ProducedTariff2, TarrifIndicator, Temperature
  FROM gap_rows
 WHERE NOT EXISTS (
        SELECT 1 FROM EnergyHistory e
         WHERE e.MeterId IS gap_rows.MeterId
           AND datetime(e.Time) = datetime(gap_rows.Time));
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Removes the reconstructed rows. Anything the app stored itself in that window
            // would go with them, but by definition there was nothing — that was the gap.
            migrationBuilder.Sql($@"
DELETE FROM EnergyHistory
 WHERE datetime(Time) >  datetime('{GapEnd}', '-36 hours')
   AND datetime(Time) <  datetime('{GapEnd}');
");
        }
    }
}
