using Microsoft.AspNetCore.Mvc;
using SessyController.Services;
using SessyController.Services.Items;
using static P1MeterService;
using static SessyController.Services.WeatherService;

namespace SessyWeb.Controllers
{
    /// <summary>
    /// This class is the main API entry for testing all functionality.
    /// </summary>
    [ApiController]
    [Route("[controller]")]
    public class BatteryManagementController : ControllerBase
    {
        private readonly ILogger<BatteryManagementController> _logger;
        private readonly BatteriesService _batteriesService;
        private readonly DayAheadMarketService _dayAheadMarketService;
        private readonly SessyService _sessyService;
        private readonly SolarEdgeService _solarEdgeService;
        private readonly P1MeterService _p1MeterService;
        private readonly WeatherService _weatherService;

        public BatteryManagementController(DayAheadMarketService DayAheadMarketService,
                                           BatteriesService batteriesService,
                                           SessyService sessyService,
                                           SolarEdgeService solarEdgeService,
                                           P1MeterService p1MeterService,
                                           WeatherService weatherService,
                                           ILogger<BatteryManagementController> logger)
        {
            _logger = logger;
            _batteriesService = batteriesService;
            _dayAheadMarketService = DayAheadMarketService;
            _sessyService = sessyService;
            _solarEdgeService = solarEdgeService;
            _p1MeterService = p1MeterService;
            _weatherService = weatherService;
        }

        #region SessyController

        /// <summary>
        /// Gets the prices fetched by the background service.
        /// </summary>
        [HttpGet("SessyController", Name = "GetHourlyInfos")]
        public List<HourlyInfo>? GetHourlyInfos()
        {
            return _batteriesService.GetHourlyInfos();
        }

        #endregion

        #region ENTSO-E

        /// <summary>
        /// Gets the prices fetched by the background service.
        /// </summary>
        [HttpGet("DayAheadMarketService", Name = "GetPrizes")]
        public List<HourlyInfo> GetPrizes()
        {
            return _dayAheadMarketService.GetPrices();
        }

        #endregion

        #region SolarEdge

        /// <summary>
        /// Gets the scaled AC power output from the SolarEdge inverter.
        /// </summary>
        /// <returns>Scaled AC Power</returns>
        [HttpGet("SolarEdgeService:GetACPowerInWatts", Name = "{id}/GetACPowerInWatts")]
        public async Task<IActionResult> GetACPowerInWatts()
        {
            var powerOutput = await _solarEdgeService.GetACPower();
            var scaleFactor = await _solarEdgeService.GetACPowerScaleFactor();

            return Ok(powerOutput * Math.Pow(10, scaleFactor));
        }

        /// <summary>
        /// Gets the unscaled AC Power from the SolarEdge inverter.
        /// </summary>
        /// <returns>Unscaled AC Power</returns>
        [HttpGet("SolarEdgeService:GetACPower", Name = "{id}/GetACPower")]
        public async Task<IActionResult> GetACPower()
        {
            ushort scaleFactor = await _solarEdgeService.GetACPower();

            return Ok(scaleFactor);
        }

        /// <summary>
        /// Gets the AC power scale factor.
        /// </summary>
        /// <returns>Scale factor</returns>
        [HttpGet("SolarEdgeService:GetACPowerScaleFactor", Name = "{id}/GetACPowerScaleFactor")]
        public async Task<IActionResult> GetACPowerScaleFactor()
        {
            short scaleFactor = await _solarEdgeService.GetACPowerScaleFactor();

            return Ok(scaleFactor);
        }

        /// <summary>
        /// Enable dynamic power limit.
        /// </summary>
        /// <returns>Scale factor</returns>
        [HttpPut("SolarEdgeService:EnableDynamicPower", Name = "{id}/EnableDynamicPower")]
        public async Task<IActionResult> EnableDynamicPower()
        {
            await _solarEdgeService.EnableDynamicPower();

            return new OkResult();
        }

        /// <summary>
        /// Restore dynamic power limit  settings.
        /// </summary>
        [HttpPut("SolarEdgeService:RestoreDynamicPowerSettings", Name = "{id}/RestoreDynamicPowerSettings")]
        public async Task<IActionResult> RestoreDynamicPowerSettings()
        {
            await _solarEdgeService.RestoreDynamicPowerSettings();

            return new OkResult();
        }

        /// <summary>
        /// Gets the ActiveReactivePreference
        /// </summary>
        [HttpGet("SolarEdgeService:GetActiveReactivePreference", Name = "{id}/GetActiveReactivePreference")]
        public async Task<IActionResult> GetActiveReactivePreference()
        {
            ushort preference = await _solarEdgeService.GetActiveReactivePreference();

            return Ok(preference);
        }

        /// <summary>
        /// Gets the ActiveReactivePreference
        /// </summary>
        [HttpGet("SolarEdgeService:GetCosPhiQPreference", Name = "{id}/GetCosPhiQPreference")]
        public async Task<IActionResult> GetCosPhiQPreference()
        {
            ushort preference = await _solarEdgeService.GetCosPhiQPreference();

            return Ok(preference);
        }

        /// <summary>
        /// Gets the Active power limit.
        /// </summary>
        [HttpGet("SolarEdgeService:GetActivePowerLimit", Name = "{id}/GetActivePowerLimit")]
        public async Task<IActionResult> GetActivePowerLimit()
        {
            float powerLimit = await _solarEdgeService.GetActivePowerLimit();

            return Ok(powerLimit);
        }

        /// <summary>
        /// Gets the reactive power limit.
        /// </summary>
        [HttpGet("SolarEdgeService:GetReactivePowerLimit", Name = "{id}/GetReactivePowerLimit")]
        public async Task<IActionResult> GetReactivePowerLimit()
        {
            float powerLimit = await _solarEdgeService.GetReactivePowerLimit();

            return Ok(powerLimit);
        }

        /// <summary>
        /// Gets the dynamic active power limit.
        /// </summary>
        [HttpGet("SolarEdgeService:GetDynamicActivePowerLimit", Name = "{id}/GetDynamicActivePowerLimit")]
        public async Task<IActionResult> GetDynamicActivePowerLimit()
        {
            float powerLimit = await _solarEdgeService.GetDynamicActivePowerLimit();

            return Ok(powerLimit);
        }

        /// <summary>
        /// Sets the dynamic active power limit.
        /// </summary>
        [HttpPut("SolarEdgeService:SetDynamicActivePowerLimit", Name = "{id}/SetDynamicActivePowerLimit")]
        public async Task<IActionResult> SetDynamicActivePowerLimit(ushort power)
        {
            await _solarEdgeService.SetDynamicActivePowerLimit(power);

            return new OkResult();
        }

        /// <summary>
        /// Sets the dynamic reactive power limit.
        /// </summary>
        [HttpPut("SolarEdgeService:SetDynamicReactivePowerLimit", Name = "{id}/SetDynamicReactivePowerLimit")]
        public async Task<IActionResult> SetDynamicReactivePowerLimit(ushort power)
        {
            await _solarEdgeService.SetDynamicReactivePowerLimit(power);

            return new OkResult();
        }

        /// <summary>
        /// Sets the active power limit.
        /// </summary>
        [HttpPut("SolarEdgeService:SetActivePowerLimit", Name = "{id}/SetActivePowerLimit")]
        public async Task<IActionResult> SetActivePowerLimit(ushort power)
        {
            await _solarEdgeService.SetActivePowerLimit(power);

            return new OkResult();
        }

        /// <summary>
        /// Retrieves the current operational status of the inverter.
        /// </summary>
        [HttpGet("SolarEdgeService:GetStatus", Name = "{id}/GetStatus")]
        public async Task<IActionResult> GetStatus()
        {
            ushort registers = await _solarEdgeService.GetStatus();

            return Ok(registers);
        }

        #endregion

        #region Sessy

        /// <summary>
        /// Gets the power status for 1 Sessy battery.
        /// </summary>
        /// <param name="id">The Id in the Appsettings.json for the Sessy battery</param>
        /// <returns></returns>
        [HttpGet("SessyService:PowerStatus", Name = "{id}/PowerStatus")]
        public async Task<IActionResult> GetPowerStatus(string id)
        {
            PowerStatus? status = await _sessyService.GetPowerStatusAsync(id);

            return Ok(status);
        }

        /// <summary>
        /// Gets the current power strategy for 1 Sessy battery.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [HttpGet("SessyService:GetActivePowerStrategy", Name = "{id}/GetActivePowerStrategy")]
        public async Task<IActionResult> GetActivePowerStrategy(string id)
        {
            ActivePowerStrategy? status = await _sessyService.GetActivePowerStrategyAsync(id);

            return Ok(status);
        }

        /// <summary>
        /// Gets the current power strategy for 1 Sessy battery.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [HttpGet("SessyService:SetPowerSetpoint", Name = "{id}/SetPowerSetpoint")]
        public async Task<IActionResult> SetPowerSetpoint(string id, int setpoint)
        {
            var powerSetpoint = new PowerSetpoint { Setpoint = setpoint };
            await _sessyService.SetPowerSetpointAsync(id, powerSetpoint);

            return new OkResult();
        }

        /// <summary>
        /// Gets the current dynamic schedule.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [HttpGet("SessyService:GetDynamicSchedule", Name = "{id}/GetDynamicSchedule")]
        public async Task<IActionResult> GetDynamicSchedule(string id)
        {
            DynamicStrategy? status = await _sessyService.GetDynamicScheduleAsync(id);

            return Ok(status);
        }

        #endregion

        #region P1 Meter

        /// <summary>
        /// Get the details of the P1 Metere
        /// </summary>
        [HttpGet("P1MeterService:GetP1Details", Name = "{id}/GetP1Details")]
        public async Task<IActionResult> GetP1Details(string id)
        {
            P1Details? registers = await _p1MeterService.GetP1DetailsAsync(id);

            return Ok(registers);
        }

        /// <summary>
        /// Get the grid target value set on the P1 Meter
        /// </summary>
        [HttpGet("P1MeterService:GetGridTarget", Name = "{id}/GetGridTarget")]
        public async Task<IActionResult> GetGridTarget(string id)
        {
            GridTargetGet? registers = await _p1MeterService.GetGridTargetAsync(id);

            return Ok(registers);
        }

        /// <summary>
        /// Set the grid target value set on the P1 Meter
        /// </summary>
        /// <remarks>
        /// Setting it does not seem to do anything. I expected the batteries to listen
        /// to this P1 meter setting and act accordingly but they don't.
        /// </remarks>
        [HttpPost("P1MeterService:SetGridTarget", Name = "{id}/SetGridTarget")]
        public async Task SetGridTarget(string id, int gridTargetValue)
        {
            var post = new GridTargetPost()
            {
                GridTarget = gridTargetValue
            };

            await _p1MeterService.SetGridTargetAsync(id, post);
        }

        #endregion

        #region WeerOnline

        /// <summary>
        /// Gets the weatherdata from the free WeerOnline API.
        /// </summary>
        [HttpGet("SunExpectancyService:GetWeatherData", Name = "GetWeatherData")]
        public IActionResult GetWeatherData()
        {
            WeerData? weatherData = _weatherService.GetWeatherData();

            return Ok(weatherData);
        }

        #endregion
    }
}
