using Microsoft.Extensions.Options;
using SessyController.Configurations;
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

        private IDisposable? _sessyP1ConfigSubscription { get; set; }

        private IOptionsMonitor<SessyP1Config> _sessyP1ConfigMonitor { get; set; }

        public P1MeterContainer(IOptionsMonitor<SessyP1Config> sessyP1ConfigMonitor,
                                P1MeterService p1MeterService)
        {
            _sessyP1ConfigMonitor = sessyP1ConfigMonitor;
            _sessyP1Config = _sessyP1ConfigMonitor.CurrentValue;
            _p1MeterService = p1MeterService;

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
            foreach (var endpoint in _sessyP1Config.Endpoints)
            {
                var p1MeterId = endpoint.Key;
                var p1MeterConfig = endpoint.Value;

                P1Meters.Clear();

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
