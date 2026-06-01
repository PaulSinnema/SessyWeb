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

        public static string FileName(string path, bool writeToConsole = false)
        {
            if(IsRunningInDocker(writeToConsole))
            {
                if(path.StartsWith('.'))
                    return path.Substring(1);

                return path;
            }
            else
            {
                if(!path.StartsWith("."))
                    return "." + path;

                return path;
            }
        }

        public static string? ConnectionString(string? path, bool writeToConsole = false)
        {
            if (path != null)
            {
                var pathParts = path.Split('=');
                var databasePath = pathParts[1].TrimStart(' ');

                databasePath = FileName(databasePath);

                return pathParts[0] + "=" + databasePath;
            }

            return path;
        }
    }
}
