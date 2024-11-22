using System;
using System.Configuration;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Gibraltar.DistributedLocking.Test.Console
{
    class Program
    {
        static void Main(string[] args)
        {
            using var loggerFactory = LoggerFactory.Create(builder =>
                                                           {
                                                               builder.AddConsole();
#if DEBUG
                                                               builder.SetMinimumLevel(LogLevel.Debug);
#else
                                                               builder.SetMinimumLevel(LogLevel.Information);
#endif

                                                           });

            var logger = loggerFactory.CreateLogger<Program>();

            var configuredConnectionString = ConfigurationManager.ConnectionStrings["LockManager"];

            if (configuredConnectionString == null)
            {
                logger.LogError("No connection string configured named 'LockManager'");
                return;
            }

            var lockProvider = new SqlLockProvider(configuredConnectionString.ConnectionString, loggerFactory.CreateLogger<SqlLockProvider>());
            var lockManager = new DistributedLockManager(lockProvider, loggerFactory.CreateLogger<DistributedLockManager>());

            logger.LogInformation("Configuring Test");

            int tasks = 100;
            bool highContention = false;

            TimeSpan maxLockDuration;
            TimeSpan lockTimeout;
            int maxLockNumber;

            if (highContention)
            {
                maxLockDuration = new TimeSpan(0, 0, 0, 0, 50);
                lockTimeout = new TimeSpan(0, 1, 0);
                maxLockNumber = tasks / 2;
            }
            else
            {
                maxLockDuration = new TimeSpan(0, 0, 0, 0, 50);
                lockTimeout = new TimeSpan(0, 2, 00);
                maxLockNumber = tasks * 5;
            }

            var lockingClient = new LockingClient(lockManager, maxLockDuration, lockTimeout, 
                "Session~d9a84ccf-9bef-4777-b202-a4343d35089a", maxLockNumber, loggerFactory.CreateLogger<LockingClient>());

            try
            {
                lockingClient.Start(tasks);
                logger.LogInformation("Running Lock Test, press any key to exit");
                System.Console.ReadKey(true);

                logger.LogInformation("Shutting down lock test");
            }
            catch (Exception ex)
            {
                System.Console.WriteLine(ex);
            }
            finally
            {
                lockingClient.Stop();
                Thread.Sleep(new TimeSpan(0, 0, 10));
                logger.LogInformation("Exiting Lock Test");
            }
        }
    }
}
