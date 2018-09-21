using System;
using System.Configuration;
using System.Security.Cryptography;
using System.Threading;

namespace Gibraltar.DistributedLocking.Test.Console
{
    class Program
    {
        static void Main(string[] args)
        {
            var configuredConnectionString = ConfigurationManager.ConnectionStrings["LockManager"];

            if (configuredConnectionString == null)
            {
                System.Console.WriteLine("No connection string configured named 'LockManager'");
                return;
            }

            var lockProvider = new SqlLockProvider(configuredConnectionString.ConnectionString);
            var lockManager = new DistributedLockManager(lockProvider);

            System.Console.WriteLine("Configuring Test");

            var rng = new RNGCryptoServiceProvider();

            int tasks = 100;
            bool highContention = false;

            TimeSpan maxLockDuration;
            TimeSpan lockTimeout;
            int maxLockNumber;

            if (highContention)
            {
                maxLockDuration = new TimeSpan(0, 0, 0, 0, 50);
                lockTimeout = new TimeSpan(0, 1, 0);
                maxLockNumber = 5;
            }
            else
            {
                maxLockDuration = new TimeSpan(0, 0, 10);
                lockTimeout = new TimeSpan(0, 2, 00);
                maxLockNumber = 250;
            }

            var lockingClient = new LockingClient(lockManager, rng, maxLockDuration, lockTimeout, "Session~d9a84ccf-9bef-4777-b202-a4343d35089a", maxLockNumber);

            try
            {
                lockingClient.Start(tasks);
                System.Console.WriteLine("Running Lock Test, press any key to exit");
                System.Console.ReadKey(true);

                System.Console.WriteLine("Shutting down lock test");
            }
            catch (Exception ex)
            {
                System.Console.WriteLine(ex);
            }
            finally
            {
                lockingClient.Stop();
                Thread.Sleep(new TimeSpan(0, 0, 10));
                System.Console.WriteLine("Exiting Lock Test");                
            }
        }
    }
}
