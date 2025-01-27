﻿using Microsoft.AspNetCore.Components;
using SessyController.Services;
using SessyController.Services.Items;

namespace SessyWeb.Pages
{
    public partial class ChargingHours : PageBase
    {
        [Inject]
        public BatteriesService? BatteriesService { get; set; }

        public List<HourlyPrice>? HourlyPrices { get; set; }

        private CancellationTokenSource _cts = new();

        protected override async Task OnInitializedAsync()
        {
            HourlyPrices = BatteriesService?.GetHourlyPrices();

            await StartLoop();

            await base.OnInitializedAsync();
        }

        private async Task StartLoop()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

            try
            {
                while (await timer.WaitForNextTickAsync(_cts.Token))
                {
                    // Zorg ervoor dat de UI wordt bijgewerkt in de render-thread
                    await InvokeAsync(() =>
                    {
                        HourlyPrices = BatteriesService?.GetHourlyPrices()?.ToList();
                        StateHasChanged();
                    });
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Timer gestopt.");
            }
        }

        public string FormatAsPrice(object value)
        {
            if (value is double)
            {
                var price = (double)value;

                return $"{price:c3}";
            }

            return "";
        }

        public string FormatAsDayHour(object value)
        {
            if (value is DateTime)
            { 
                var dateTime = (DateTime)value;

                return $"{dateTime.Day}-{dateTime.Month}/{dateTime.Hour}";
            }

            return "";
        }
    }
}
