
namespace SessyController.Services
{
    public abstract class  BackgroundHeartbeatService : BackgroundService
    {
        public delegate Task BackgroundHeartBeatDelegate();

        public event BackgroundHeartBeatDelegate? OnHeartBeat;

        protected override Task ExecuteAsync(CancellationToken stoppingToken) { return Task.CompletedTask; }

        public async Task HeartBeatAsync()
        {
            if(OnHeartBeat != null)
                await OnHeartBeat?.Invoke();
        }
    }
}
