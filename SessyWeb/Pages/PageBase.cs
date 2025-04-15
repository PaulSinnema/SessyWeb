﻿using Microsoft.AspNetCore.Components;
using SessyController.Services.Items;

namespace SessyWeb.Pages
{
    public class PageBase : ComponentBase, IDisposable
    {
        [Inject]
        public BatteryContainer? batteryContainer { get; set; }

        [CascadingParameter]
        public bool IsMobile { get; set; }

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

