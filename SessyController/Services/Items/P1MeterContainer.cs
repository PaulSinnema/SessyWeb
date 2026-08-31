using Microsoft.Extensions.Options;
using SessyCommon.Configurations;
using SessyCommon.Services;
using static P1MeterService;

namespace SessyController.Services.Items
{
    /// <summary>
    /// This class contains all P1 Meter configurations.
    /// </summary>
    public class P1MeterContainer : IDisposable
    {
        private SessyP1Config _sessyP1Config { get; set; }
        private P1MeterService _p1MeterService { get; set; }
        private LoggingService<P1MeterContainer> _logger { get; set; }

        private IDisposable? _sessyP1ConfigSubscription { get; set; }

        private IOptionsMonitor<SessyP1Config> _sessyP1ConfigMonitor { get; set; }

        public P1MeterContainer(IOptionsMonitor<SessyP1Config> sessyP1ConfigMonitor,
                                P1MeterService p1MeterService,
                                LoggingService<P1MeterContainer> logger)
        {
            _sessyP1ConfigMonitor = sessyP1ConfigMonitor;
            _sessyP1Config = _sessyP1ConfigMonitor.CurrentValue;
            _p1MeterService = p1MeterService;
            _logger = logger;

            _sessyP1ConfigSubscription = _sessyP1ConfigMonitor.OnChange((settings) =>
            {
                _sessyP1Config = settings;

                AddMeters();
            });

            AddMeters();
        }

        /// <summary>
        /// Add all P1 Meter configurations to the list.
        /// </summary>
        private void AddMeters()
        {
            // Cleared once, not per meter: inside the loop only the last endpoint survived, and a
            // config reload with an empty section appended to the old list instead of emptying it.
            P1Meters.Clear();

            foreach (var endpoint in _sessyP1Config.Endpoints)
            {
                var p1MeterId = endpoint.Key;
                var p1MeterConfig = endpoint.Value;

                // Credentials in secrets.json for a meter that appsettings.json no longer declares
                // merge into a device with no address — see SessyBatteryEndpoint.IsConfigured.
                if (!p1MeterConfig.IsConfigured)
                    continue;

                var p1Meter = new P1Meter()
                {
                    Id = p1MeterId,
                    UserId = p1MeterConfig.UserId,
                    Password = p1MeterConfig.Password,
                    Name = p1MeterConfig.Name,
                    BaseUrl = p1MeterConfig.BaseUrl
                };

                AddMeter(p1Meter);
            }
        }

        public List<P1Meter>? P1Meters { get; set; } = new List<P1Meter>();

        /// <summary>
        /// Add a P1 Meter configuration to the list.
        /// </summary>
        private void AddMeter(P1Meter p1Meter)
        {
            P1Meters.Add(p1Meter);
        }

        /// <summary>
        /// Get the details from the P1 meter.
        /// </summary>
        public async Task<P1Details?> GetDetails(string id)
        {
            return await _p1MeterService!.GetP1DetailsAsync(id);
        }

        /// <summary>
        /// Net power (W) of the first configured P1 meter. Import +, export -.
        /// One grid connection = one meter; returns null when none is configured.
        /// </summary>
        public async Task<double?> GetFirstMeterNetPowerAsync()
        {
            var meter = FirstMeterOrNull();
            if (meter == null) return null;

            var details = await GetDetails(meter.Name!);
            return details?.PowerTotal;
        }

        /// <summary>
        /// Set the grid target (W) on the first configured P1 meter. Import +, export -.
        /// The control-mode gate lives in P1MeterService.SetGridTargetAsync.
        /// </summary>
        public async Task SetGridTargetFirstAsync(int gridTargetW)
        {
            var meter = FirstMeterOrNull();
            if (meter == null) return;

            await _p1MeterService.SetGridTargetAsync(meter.Name!, new GridTargetPost { GridTarget = gridTargetW });
        }

        /// <summary>
        /// The grid target (W) currently set on the first P1 meter, for display/verification.
        /// Import +, export -. Null when no meter is configured. Reads silently — no Warning.
        /// </summary>
        public async Task<int?> GetFirstMeterGridTargetAsync()
        {
            var meter = P1Meters?.FirstOrDefault();
            if (meter == null) return null;

            var target = await _p1MeterService.GetGridTargetAsync(meter.Name!);
            return target?.GridTarget;
        }

        // One grid connection = one meter. Warn on none or extras; take the first.
        private P1Meter? FirstMeterOrNull()
        {
            if (P1Meters == null || P1Meters.Count == 0)
            {
                _logger.LogWarning("No P1 meter configured; grid target cannot be set.");
                return null;
            }

            if (P1Meters.Count > 1)
                _logger.LogWarning($"Multiple P1 meters configured ({P1Meters.Count}); using the first ({P1Meters[0].Name}) for grid target.");

            return P1Meters[0];
        }

        private bool _isDisposed = false;

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _sessyP1ConfigSubscription.Dispose();
                P1Meters.Clear();
                P1Meters = null;
                _isDisposed = true;
            }
        }

        internal async Task<double> GetTotalPowerInWatts()
        {
            var powerInWatts = 0.0;

            foreach (var meter in P1Meters)
            {
                var details = await GetDetails(meter!.Name!);

                if (details != null)
                {
                    powerInWatts += details.PowerTotal;
                }
            }

            return powerInWatts;
        }
    }
}
