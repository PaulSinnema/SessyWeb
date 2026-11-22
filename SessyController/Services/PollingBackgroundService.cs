namespace SessyController.Services
{
    /// <summary>Non-generic view of LoggingService, so shared base classes can log for any service.</summary>
    public interface ILog
    {
        void LogInformation(string? message);
        void LogWarning(string? message);
        void LogError(string? message);
        void LogException(Exception ex, string? message);
    }

    /// <summary>
    /// A background service that runs one cycle, waits and repeats until shutdown. Start, stop and
    /// cycle errors are logged here, so a failing cycle never ends the loop.
    /// </summary>
    public abstract class PollingBackgroundService : BackgroundService
    {
        protected readonly ILog Log;

        protected PollingBackgroundService(ILog log)
        {
            Log = log;
        }

        /// <summary>Name used in the start, stop and error log lines.</summary>
        protected abstract string Name { get; }

        /// <summary>Wait between cycles; may depend on the clock or on the last result.</summary>
        protected abstract TimeSpan NextDelay();

        /// <summary>True to wait before each cycle instead of after it.</summary>
        protected virtual bool DelayFirst => false;

        /// <summary>One unit of work. An exception is logged by OnCycleError and the loop goes on.</summary>
        protected abstract Task RunCycleAsync(CancellationToken cancellationToken);

        /// <summary>Runs once before the first cycle.</summary>
        protected virtual Task OnStartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>Runs once after the loop has ended.</summary>
        protected virtual Task OnStoppedAsync() => Task.CompletedTask;

        protected virtual void OnCycleError(Exception ex) => Log.LogException(ex, $"{Name}: cycle failed.");

        protected sealed override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            await OnStartingAsync(cancellationToken).ConfigureAwait(false);

            Log.LogWarning($"{Name} started ...");

            while (!cancellationToken.IsCancellationRequested)
            {
                if (DelayFirst && !await WaitAsync(cancellationToken).ConfigureAwait(false))
                    break;

                try
                {
                    await RunCycleAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnCycleError(ex);
                }

                if (!DelayFirst && !await WaitAsync(cancellationToken).ConfigureAwait(false))
                    break;
            }

            await OnStoppedAsync().ConfigureAwait(false);

            Log.LogWarning($"{Name} stopped.");
        }

        private async Task<bool> WaitAsync(CancellationToken cancellationToken)
        {
            var delay = NextDelay();

            try
            {
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}
