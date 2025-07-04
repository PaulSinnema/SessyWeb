﻿using Microsoft.AspNetCore.Components;
using SessyController.Services.Items;
using SessyWeb.Helpers;
using SessyWeb.Services;

namespace SessyWeb.Pages
{
    public class PageBase : ComponentBase, IDisposable
    {
        [Inject]
        public BatteryContainer? batteryContainer { get; set; }
        [Inject]
        public ScreenSizeService? _screenSizeService { get; set; }

        [CascadingParameter(Name = "ScreenInfo")]
        public ScreenInfo? ScreenInfo { get; set; }

        public bool IsComponentActive { get; internal set; } = false;

        protected override void OnInitialized()
        {
            IsComponentActive = true;
        }

        private bool _isDisposed = false;

        public virtual void Dispose()
        {
            if (!_isDisposed)
            {
                IsComponentActive = false;

                _isDisposed = true;
            }
        }

        public IFormatProvider GetFormatProvider()
        {
            return new System.Globalization.CultureInfo("nl-NL");
        }
    }
}

