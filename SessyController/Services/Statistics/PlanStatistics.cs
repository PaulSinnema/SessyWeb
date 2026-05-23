namespace SessyController.Services.Statistics
{
    /// <summary>
    /// Statistics about the current MILP plan for display on the dashboard.
    /// </summary>
    public class PlanStatistics
    {
        /// <summary>When the plan was last built (null when restored from DB without rebuild).</summary>
        public DateTime? LastBuildTime { get; set; }

        /// <summary>True when the plan was restored from the database after a restart.</summary>
        public bool IsRestoredFromDb { get; set; }

        /// <summary>Last quarter in the current plan.</summary>
        public DateTime? PlanHorizon { get; set; }

        /// <summary>Total number of future quarters in the plan.</summary>
        public int TotalFutureQuarters { get; set; }

        /// <summary>Number of future quarters planned as Charging.</summary>
        public int ChargingQuarters { get; set; }

        /// <summary>Number of future quarters planned as Discharging.</summary>
        public int DischargingQuarters { get; set; }

        /// <summary>Number of future quarters planned as Zero Net Home.</summary>
        public int NzhQuarters { get; set; }

        /// <summary>Expected arbitrage profit for the remaining plan (EUR).</summary>
        public double ExpectedProfitEur { get; set; }

        /// <summary>Next planned discharge start time (null if none planned).</summary>
        public DateTime? NextDischargeTime { get; set; }

        /// <summary>Next planned charge start time (null if none planned).</summary>
        public DateTime? NextChargeTime { get; set; }

        /// <summary>SOC deviation at last build as percentage of total capacity.</summary>
        public double SocDeviationPct { get; set; }
    }
}