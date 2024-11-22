using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Gibraltar.DistributedLocking.Test.Console
{
    /// <summary>
    /// Generates locking requests for resources and releases them after a period of time.
    /// </summary>
    public class LockingClient
    {
        private readonly DistributedLockManager _lockManager;
        private readonly TimeSpan _maxLockDuration;
        private readonly TimeSpan _lockTimeout;
        private readonly string _lockPrefix;
        private readonly int _maxLockNumber;
        private readonly ILogger<LockingClient> _logger;
        private readonly ConsoleColor _defaultForeground;

#if NETFRAMEWORK
        private readonly RandomNumberGenerator _rng;
#endif

        private CancellationTokenSource _cancellationTokenSource;
        private Task[] _clientTasks;


        public LockingClient(DistributedLockManager lockManager, TimeSpan maxLockDuration, TimeSpan lockTimeout, string lockPrefix, 
            int maxLockNumber, ILogger<LockingClient> logger)
        {
            _lockManager = lockManager;
            _maxLockDuration = maxLockDuration;
            _lockTimeout = lockTimeout;
            _lockPrefix = lockPrefix;
            _maxLockNumber = maxLockNumber;
            _logger = logger;

#if NETFRAMEWORK
            _rng = RandomNumberGenerator.Create();
#endif
            _defaultForeground = System.Console.ForegroundColor;
        }

        /// <summary>
        /// Start processing locks with the specified number of locking tasks
        /// </summary>
        /// <param name="tasks"></param>
        public void Start(int tasks)
        {
            if (_cancellationTokenSource != null)
                throw new InvalidOperationException("Client is already started");

            _cancellationTokenSource = new CancellationTokenSource();

            _clientTasks = new Task[ tasks ];

            for (var index = 0; index < _clientTasks.Length; index++)
            {
                _clientTasks[index] = Task.Factory.StartNew(() => GenerateLocks( _cancellationTokenSource), TaskCreationOptions.LongRunning);
            }
        }

        /// <summary>
        /// Stop processing locks (releasing any locks held)
        /// </summary>
        public void Stop()
        {
            if (_cancellationTokenSource != null)
            {
                _cancellationTokenSource.Cancel();

                _cancellationTokenSource = null;

            }

            _clientTasks = null;
        }

        private void GenerateLocks( CancellationTokenSource cancellationTokenSource)
        {
            try
            {
                while (cancellationTokenSource.IsCancellationRequested == false)
                {
#if NETFRAMEWORK
                    var lockTimespan = new TimeSpan(GetRandomNumber(0, (int)_maxLockDuration.Ticks));
                    var nameIndex = GetRandomNumber(1, _maxLockNumber).ToString();
#else
                    var lockTimespan = new TimeSpan(RandomNumberGenerator.GetInt32(0, (int)_maxLockDuration.Ticks));
                    var nameIndex = RandomNumberGenerator.GetInt32(1, _maxLockNumber).ToString();
#endif
                    var name = _lockPrefix + "-" + nameIndex;
                    try
                    {
                        var stopwatch = Stopwatch.StartNew();
                        using (var newLock = _lockManager.Lock(this, name, (int)_lockTimeout.TotalSeconds))
                        {
                            stopwatch.Stop();

                            _logger.LogDebug("{4} Thread {1} - Acquired lock {0} in {2:N0}ms, will hold for {3:N0}ms", 
                                newLock.Name, Thread.CurrentThread.ManagedThreadId, stopwatch.ElapsedMilliseconds, lockTimespan.TotalMilliseconds, DateTime.Now);

                            Thread.Sleep(lockTimespan);

                            //Task.Delay(lockTimespan, cancellationTokenSource.Token).Wait(cancellationTokenSource.Token);
                            _logger.LogDebug("{2} Thread {1} - Releasing lock {0}", newLock.Name, Thread.CurrentThread.ManagedThreadId, DateTime.Now);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError("{3} Thread {1} - Unable to acquire lock {0} due to {2}",
                            name, Thread.CurrentThread.ManagedThreadId, ex.GetBaseException().GetType().Name, DateTime.Now);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("{3} Thread {1} - Locking task failed due to {0}:\r\n{2}",
                    ex.GetBaseException().GetType().Name, Thread.CurrentThread.ManagedThreadId, ex.StackTrace, DateTime.Now);
            }
        }

#if NETFRAMEWORK
        private int GetRandomNumber(int minValue, int maxValue)
        {
            lock (_rng)
            {
                var randomBytes = new Byte[4];
                _rng.GetBytes(randomBytes);
                var randomInt = Math.Abs(BitConverter.ToInt32(randomBytes, 0));

                if (randomInt != 0)
                {
                    randomInt = minValue + (randomInt % (maxValue - minValue));
                }

                return randomInt;
            }
        }
#endif
    }
}
