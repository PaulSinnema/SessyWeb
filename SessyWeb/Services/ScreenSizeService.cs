using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace SessyWeb.Services
{
    public class ScreenSizeService : IAsyncDisposable
    {
        private readonly IJSRuntime _jsRuntime;
        private IJSObjectReference? _module;
        private DotNetObjectReference<ScreenSizeService>? _dotNetRef;

        public event Action<int, int>? OnScreenSizeChanged;

        public ScreenSizeService(IJSRuntime jsRuntime)
        {
            _jsRuntime = jsRuntime;
        }

        private async Task EnsureScreensizeJsLoaded()
        {
            if(_module == null)
                _module = await _jsRuntime.InvokeAsync<IJSObjectReference>("import", "./screensize.js");
        }

        public async Task InitializeAsync()
        {
            _dotNetRef = DotNetObjectReference.Create(this);
            await EnsureScreensizeJsLoaded();
            await _module!.InvokeVoidAsync("initialize", _dotNetRef);
        }

        [JSInvokable]
        public void UpdateScreenSize(int height, int width)
        {
            OnScreenSizeChanged?.Invoke(height, width);
        }

        public async ValueTask<int> GetScreenHeightAsync()
        {
            await EnsureScreensizeJsLoaded();

            return await _module!.InvokeAsync<int>("getScreenHeight");
        }

        public async ValueTask<int> GetScreenWidthAsync()
        {
            await EnsureScreensizeJsLoaded();

            return await _module!.InvokeAsync<int>("getScreenWidth");
        }

        public async ValueTask<int> GetElementWidth(ElementReference element)
        {
            await EnsureScreensizeJsLoaded();

            const int maxAttempts = 5;
            const int delayMs = 100;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    return await _module!.InvokeAsync<int>("getElementWidth", element);
                }
                catch (JSException ex) when (ex.Message.Contains("getElementWidth"))
                {
                    if (attempt == maxAttempts)
                        throw new InvalidOperationException("JavaScript function 'getElementWidth' not found after retries.", ex);

                    await Task.Delay(delayMs);
                }
            }

            throw new InvalidOperationException("Unexpected failure while calling 'getElementWidth'");
        }

        private bool _IsDisposed = false;

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_IsDisposed)
                {
                    if (_module != null)
                    {
                        await _module.DisposeAsync();
                    }

                    _dotNetRef?.Dispose();

                    _IsDisposed = true;
                }
            }
            catch (Exception)
            {
                // Keep it silent.
            }
        }
    }
}
