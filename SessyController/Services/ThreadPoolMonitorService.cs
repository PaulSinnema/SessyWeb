using Microsoft.Extensions.Hosting;

namespace SessyController.Services
{
    /// <summary>
    /// Samples the thread pool and reports starvation.
    ///
    /// A blocked thread pool is what a Blazor Server UI feels as "every click takes seconds": the
    /// SignalR messages that carry the clicks are queued behind work items that cannot start,
    /// because the pool only grows by roughly one thread per second once its minimum is used up.
    /// The signature is a work queue that stays non-empty while the thread count climbs.
    ///
    /// Reports only when the queue is above the threshold, so a healthy system stays silent.
    /// </summary>
    public class ThreadPoolMonitorService : BackgroundService
    {
        private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);

        /// <summary>Queued work items above this are worth a line in the log.</summary>
        private const int PendingThreshold = 8;

        private readonly LoggingService<ThreadPoolMonitorService> _logger;

        public ThreadPoolMonitorService(LoggingService<ThreadPoolMonitorService> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(SampleInterval);

            long previousCompleted = ThreadPool.CompletedWorkItemCount;

            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    long pending = ThreadPool.PendingWorkItemCount;
                    long completed = ThreadPool.CompletedWorkItemCount;

                    if (pending >= PendingThreshold)
                    {
                        ThreadPool.GetMinThreads(out int minWorker, out _);

                        _logger.LogWarning(
                            $"ThreadPool busy: {pending} queued, {ThreadPool.ThreadCount} threads " +
                            $"(min {minWorker}), {completed - previousCompleted} completed in the last " +
                            $"{SampleInterval.TotalSeconds:F0}s.");
                    }

                    previousCompleted = completed;
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
        }
    }
}
