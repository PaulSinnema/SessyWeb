using Microsoft.JSInterop;

namespace SessyWeb.Services
{
    public class ScreenSizeService : IAsyncDisposable
    {
        private readonly IJSRuntime _jsRuntime;
        private IJSObjectReference? _module;
        private DotNetObjectReference<ScreenSizeService>? _dotNetRef;

        public event Action<int>? OnScreenHeightChanged;

        public ScreenSizeService(IJSRuntime jsRuntime)
        {
            _jsRuntime = jsRuntime;
        }

        public async Task InitializeAsync()
        {
            _dotNetRef = DotNetObjectReference.Create(this);
            _module = await _jsRuntime.InvokeAsync<IJSObjectReference>("import", "./screensize.js");
            await _module.InvokeVoidAsync("initialize", _dotNetRef);
        }

        [JSInvokable]
        public void UpdateScreenHeight(int height)
        {
            OnScreenHeightChanged?.Invoke(height);
        }

        public async ValueTask<int> GetScreenHeightAsync()
        {
            if (_module == null)
            {
                _module = await _jsRuntime.InvokeAsync<IJSObjectReference>("import", "./screensize.js");
            }
            return await _module.InvokeAsync<int>("getScreenHeight");
        }

        public async ValueTask DisposeAsync()
        {
            if (_module != null)
            {
                await _module.DisposeAsync();
            }
            _dotNetRef?.Dispose();
        }
    }
}
