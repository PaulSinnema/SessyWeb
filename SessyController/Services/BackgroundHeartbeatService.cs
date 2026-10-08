
namespace SessyController.Services
{
    public abstract class  BackgroundHeartbeatService : BackgroundService
    {
        public delegate Task BackgroundHeartBeatDelegate();

        public event BackgroundHeartBeatDelegate? OnHeartBeat;

        /// <summary>Time of the last beat, so a page can tell a stalled loop from a quiet one.</summary>
        public DateTime? LastHeartbeatUtc { get; private set; }

        protected override Task ExecuteAsync(CancellationToken stoppingToken) { return Task.CompletedTask; }

        public async Task HeartBeatAsync()
        {
            LastHeartbeatUtc = DateTime.UtcNow;
            if(OnHeartBeat != null)
                await OnHeartBeat?.Invoke();
        }
    }
}
