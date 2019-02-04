using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Gibraltar.DistributedLocking.Test.Console
{
    /// <summary>
    /// Generates locking requests for resources and releases them after a period of time.
    /// </summary>
    public class LockingClient
    {
        private readonly DistributedLockManager _lockManager;
        private readonly RandomNumberGenerator _rng;
        private readonly TimeSpan _maxLockDuration;
        private readonly TimeSpan _lockTimeout;
        private readonly string _lockPrefix;
        private readonly int _maxLockNumber;

        private CancellationTokenSource _cancellationTokenSource;
        private Task[] _clientTasks;

        private ConsoleColor _defaultForeground;

        public LockingClient(DistributedLockManager lockManager, RandomNumberGenerator rng, TimeSpan maxLockDuration, TimeSpan lockTimeout, string lockPrefix, int maxLockNumber)
        {
            _lockManager = lockManager;
            _rng = rng;
            _maxLockDuration = maxLockDuration;
            _lockTimeout = lockTimeout;
            _lockPrefix = lockPrefix;
            _maxLockNumber = maxLockNumber;
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

        private int GetRandomNumber(int minValue, int maxValue)
        {
            lock(_rng)
            {
                var randomBytes = new Byte[ 4 ];
                _rng.GetBytes(randomBytes);
                var randomInt = Math.Abs( BitConverter.ToInt32(randomBytes, 0));

                if (randomInt != 0)
                {
                    randomInt = minValue + (randomInt  % (maxValue - minValue));
                }

                return randomInt;
            }
        }

        private void GenerateLocks( CancellationTokenSource cancellationTokenSource)
        {
            try
            {
                while (cancellationTokenSource.IsCancellationRequested == false)
                {
                    var lockTimespan = new TimeSpan(GetRandomNumber(0, (int)_maxLockDuration.Ticks));
                    var nameIndex = GetRandomNumber(1, _maxLockNumber).ToString();
                    var name = _lockPrefix + "-" + nameIndex;
                    try
                    {
                        var stopwatch = Stopwatch.StartNew();
                        using (var newLock = _lockManager.Lock(this, name, (int)_lockTimeout.TotalSeconds))
                        {
                            stopwatch.Stop();

#if DEBUG
                            System.Console.WriteLine("{4} Thread {1} - Acquired lock {0} in {2:N0}ms, will hold for {3:N0}ms", 
                                newLock.Name, Thread.CurrentThread.ManagedThreadId, stopwatch.ElapsedMilliseconds, lockTimespan.TotalMilliseconds, DateTime.Now);
#endif

                            Thread.Sleep(lockTimespan);

                            //Task.Delay(lockTimespan, cancellationTokenSource.Token).Wait(cancellationTokenSource.Token);
#if DEBUG
                            System.Console.WriteLine("{2} Thread {1} - Releasing lock {0}", newLock.Name, Thread.CurrentThread.ManagedThreadId, DateTime.Now);
#endif
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Console.ForegroundColor = ConsoleColor.Red;
                        System.Console.WriteLine("{3} Thread {1} - Unable to acquire lock {0} due to {2}", 
                            name, Thread.CurrentThread.ManagedThreadId, ex.GetBaseException().GetType(), DateTime.Now);
                        System.Console.ForegroundColor = _defaultForeground;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Console.ForegroundColor = ConsoleColor.Red;
                System.Console.WriteLine("{3} Thread {1} - Locking task failed due to {0}:\r\n{2}", 
                    ex.GetBaseException().GetType(), Thread.CurrentThread.ManagedThreadId, ex.StackTrace, DateTime.Now);
                System.Console.ForegroundColor = _defaultForeground;
            }
        }
    }
}
