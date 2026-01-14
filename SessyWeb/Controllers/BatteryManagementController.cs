using Microsoft.AspNetCore.Mvc;
using SessyController.Services;
using SessyController.Services.InverterServices;
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
        private readonly SolarEdgeInverterService _solarEdgeService;
        private readonly P1MeterService _p1MeterService;
        private readonly WeatherService _weatherService;

        public BatteryManagementController(DayAheadMarketService DayAheadMarketService,
                                           BatteriesService batteriesService,
                                           SessyService sessyService,
                                           SolarEdgeInverterService solarEdgeService,
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
        [HttpGet("SessyController", Name = "GetQuarterlyInfos")]
        public List<QuarterlyInfo>? GetQuarterlyInfos()
        {
            return _batteriesService.GetQuarterlyInfos();
        }

        #endregion

        #region ENTSO-E

        /// <summary>
        /// Gets the prices fetched by the background service.
        /// </summary>
        [HttpGet("DayAheadMarketService", Name = "GetPrizes")]
        public async Task<List<QuarterlyInfo>> GetPrizes()
        {
            return await _dayAheadMarketService.GetPrices();
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
            SessyScheduleResponse? status = await _sessyService.GetDynamicScheduleAsync(id);

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
        public async Task<IActionResult> GetWeatherData()
        {
            WeerData? weatherData = await _weatherService.GetWeatherData();

            return Ok(weatherData);
        }

        #endregion
    }
}
