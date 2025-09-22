namespace SessyCommon.Services
{
    public static class DockerService
    {
        /// <summary>
        /// Read the environment variable 'DOTNET_RUNNING_IN_DOCKER' to see if we are running in Docker.
        /// (See: dockerfile).
        /// </summary>
        public static bool IsRunningInDocker(bool writeToConsole = false)
        {
            bool isRunningInDocker = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_DOCKER") == "true";

            if (writeToConsole)
            {
                if (isRunningInDocker)
                {
                    Console.WriteLine("Running in Docker");
                }
                else
                {
                    Console.WriteLine("Not running in Docker");
                }
            }

            return isRunningInDocker;
        }
    }
}
