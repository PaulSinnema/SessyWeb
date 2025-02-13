﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SessyController.Configurations;
using System.Diagnostics.Eventing.Reader;
using static SessyController.Services.Items.Session;

namespace SessyController.Services.Items
{
    public class HourlyInfo
    {
        public HourlyInfo(DateTime time, double price, IServiceScopeFactory serviceScopeFactory)
        {
            Time = time;
            Price = price;
            _serviceScopeFactory = serviceScopeFactory;

            using (var scope = _serviceScopeFactory.CreateScope())
            {
                _settingsConfigMonitor = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<SettingsConfig>>();
                _sessyBatteryConfigMonitor = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<SessyBatteryConfig>>();

                _settingsConfigMonitor.OnChange((SettingsConfig settings) => _settingsConfig = settings);
                _sessyBatteryConfigMonitor.OnChange((SessyBatteryConfig settings) => _sessyBatteryConfig = settings);
            }

            _settingsConfig = _settingsConfigMonitor.CurrentValue;
            _sessyBatteryConfig = _sessyBatteryConfigMonitor.CurrentValue;
        }

        private IOptionsMonitor<SettingsConfig> _settingsConfigMonitor;
        private IOptionsMonitor<SessyBatteryConfig> _sessyBatteryConfigMonitor;
        private SettingsConfig _settingsConfig;
        private SessyBatteryConfig _sessyBatteryConfig;

        /// <summary>
        /// Price from ENTSO-E
        /// </summary>
        public double Price { get; set; }

        public double SmoothedPrice { get; private set; }

        /// <summary>
        /// Timestamp from ENTSO-E
        /// </summary>
        public DateTime Time { get; set; }

        private IServiceScopeFactory _serviceScopeFactory;

        /// <summary>
        /// How much profit does this (dis)charge give?
        /// </summary>
        public double Profit => Selling - Buying;

        public double ProfitVisual => Profit / 10;

        public double Buying { get; set; }

        public double Selling { get; set; }

        public double ChargeLeft { get; set; }

        public double ChargeLeftPercentage { get; set; }

        public double ChargeLeftVisual => ChargeLeft / 100000;

        public double SolarGlobalRadiation { get; set; }

        public double SolarPower { get; set; }

        public double SolarPowerVisual => SolarPower / 10;

        private bool _charging = false;

        /// <summary>
        /// If true charging is requested.
        /// </summary>
        public bool Charging
        {
            get
            {

                return _charging;
            }
            private set
            {
                if (value)
                {
                    _discharging = false;
                }

                _charging = value;
            }
        }


        private bool _discharging = false;

        /// <summary>
        /// If true charging is requested.
        /// </summary>
        public bool Discharging
        {
            get
            {
                return _discharging;
            }
            private set
            {

                if (value)
                {
                    _charging = false;
                }

                _discharging = value;
            }
        }

        /// <summary>
        /// Adds a gesmoothed price to each HourlyInfo object in the list.
        /// </summary>
        public static void AddSmoothedPrices(List<HourlyInfo> hourlyData, int windowSize)
        {
            if (hourlyData == null || hourlyData.Count == 0) return;

            for (int i = 0; i < hourlyData.Count; i++)
            {
                int start = Math.Max(0, i - windowSize / 2);
                int end = Math.Min(hourlyData.Count - 1, i + windowSize / 2);

                double average = hourlyData.Skip(start).Take(end - start + 1).Average(h => h.Price);
                hourlyData[i].SmoothedPrice = average;
            }
        }

        public void DisableCharging()
        {
            Charging = false;
        }

        public void DisableDischarging()
        {
            Discharging = false;
        }

        public void SetModes(Modes mode)
        {
            switch (mode)
            {
                case Modes.Charging:
                    Charging = true;
                    break;

                case Modes.Discharging:
                    Discharging = true;
                    break;

                default:
                    throw new InvalidOperationException("Mode wrong");
            }
        }

        /// <summary>
        /// Helper for visualization
        /// </summary>
        public double VisualizeInChart
        {
            get
            {
                if (Charging)
                    return -0.2;
                else if (Discharging)
                    return 0.2;
                else if (ZeroNetHome)
                    return 0.03;

                return 0.0;
            }
        }

        public string DisplayState
        {
            get
            {
                return Charging ? "Charging" :
                          Discharging ? "Discharging" :
                          ZeroNetHome ? "Net zero home" :
                          "Disabled";
            }
        }

        /// <summary>
        /// If no (dis)charging is in progress Net Zero Home is requested.
        /// </summary>
        public bool ZeroNetHome
        {
            get => !(Charging || Discharging) && Profit > _settingsConfig.NetZeroHomeMinProfit;
        }

        /// <summary>
        /// This list contains the hours found for charging against this price.
        /// </summary>
        public List<HourlyInfo>? HoursCharging { get; set; }

        /// <summary>
        /// For better debugging
        /// </summary>
        public override string ToString()
        {
            return $"{Time}: Charging: {Charging}, Discharging: {Discharging}, Zero Net Home: {ZeroNetHome}, Price: {Price}, Charge left: {ChargeLeft}";
        }
    }
}
