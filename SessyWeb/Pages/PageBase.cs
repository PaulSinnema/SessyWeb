using Microsoft.AspNetCore.Components;
using SessyController.Services;
using SessyController.Services.Items;
using SessyWeb.Helpers;
using SessyWeb.Components;

namespace SessyWeb.Pages
{
    public class PageBase : BaseComponent, IDisposable
    {
        [Inject]
        public SystemCapabilitiesService? _capabilities { get; set; }

        /// <summary>True when an inverter is configured — pages hide solar figures without one.</summary>
        public bool HasSolar => _capabilities!.HasSolar;

#if DEBUG
        public bool HideId = false;
#else
        public bool HideId = true;
#endif

        [CascadingParameter]
        public Action<bool> SetIsBusy { get; set; } = default!;

        public bool IsBusy
        {
            set
            {
                if (SetIsBusy != null)
                    SetIsBusy(value);
            }
        }

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
    }
}

