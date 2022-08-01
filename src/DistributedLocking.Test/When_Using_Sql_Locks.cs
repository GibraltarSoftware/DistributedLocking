#region File Header and License
// /*
//    When_Using_Sql_Locks.cs
//    Copyright 2008-2017 Gibraltar Software, Inc.
//    
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
// 
//        http://www.apache.org/licenses/LICENSE-2.0
// 
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.
// */
#endregion

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Gibraltar.DistributedLocking.Test
{
    [TestFixture]
    public class When_Using_Sql_Locks
    {
#if NETCOREAPP
        private const string ConnectionStringTemplate = "Data Source=tcp:{0};Initial Catalog={1};Integrated Security=False;MultipleActiveResultSets=True;User Id=sa;Password=f4rd+GM%";
#else
        private const string ConnectionStringTemplate = "Data Source={0};Initial Catalog={1};Integrated Security=False;MultipleActiveResultSets=True;" +
                                                        "Network Library=dbmssocn;User Id=sa;Password=f4rd+GM%";
#endif
        private const string MultiprocessLockName = "LockRepository";
        private const string DefaultLockDatabase = "lock_test";
        private const string SecondLockDatabase = "tempdb";
        private const string ThirdLockDatabase = "master";

        [Test]
        public void Can_Acquire_Lock()
        {
            var lockManager = new DistributedLockManager(GetLockProvider(DefaultLockDatabase));

            using (var outerLock = lockManager.Lock(this, MultiprocessLockName, 0))
            {
                Assert.IsNotNull(outerLock, "Unable to acquire lock");
            }
        }

        [Test]
        public void Can_Acquire_Lock_With_Unsafe_Name()
        {
            var lockManager = new DistributedLockManager(GetLockProvider(DefaultLockDatabase));

            var unsafeLockName = "\"M<>\"\\a/ry/ h**ad:>> a\\/:*?\"<>| li*tt|le|| la\"mb.?";

            using (var outerLock = lockManager.Lock(this, unsafeLockName, 0))
            {
                Assert.IsNotNull(outerLock, "Unable to acquire the lock");
            }
        }

        [Test]
        public void Can_Not_Acquire_Same_Lock_On_Another_Thread()
        {
            var lockManager = new DistributedLockManager(GetLockProvider(DefaultLockDatabase));

            using (var outerLock = lockManager.Lock(this, MultiprocessLockName, 0))
            {
                Assert.IsNotNull(outerLock, "Unable to acquire outer lock the repository");

                using (var otherLock = OtherThreadLockHelper.TryLock(this, lockManager, MultiprocessLockName, 0))
                {
                    Assert.IsNull(otherLock, "Another thread was allowed to get the lock");
                }
            }
        }

        [Test]
        public void Can_ReEnter_Lock_On_Same_Thread()
        {
            var lockManager = new DistributedLockManager(GetLockProvider(DefaultLockDatabase));

            // First test new re-entrant lock capability.
            using (var outerLock = lockManager.Lock(this, MultiprocessLockName, 0))
            {
                Assert.IsNotNull(outerLock, "Unable to outer lock the repository");

                // Now check that we can get the same lock on the same thread.
                using (var middleLock = lockManager.Lock(this, MultiprocessLockName, 0))
                {
                    Assert.IsNotNull(middleLock, "Unable to reenter the repository lock on the same thread");

                    using (var innerLock = lockManager.Lock(this, MultiprocessLockName, 0))
                    {
                        Assert.IsNotNull(innerLock, "Unable to reenter the repository lock on the same thread twice");
                    }
                }
            }
        }

        [Test]
        public void Can_Not_Acquire_Same_Lock_In_Same_Scope()
        {
            var lockManager = new DistributedLockManager(GetLockProvider(DefaultLockDatabase));

            // Now test other scenarios while another thread holds the lock.
            using (var testLock = OtherThreadLockHelper.TryLock(this, lockManager, MultiprocessLockName, 0))
            {
                Assert.IsNotNull(testLock, "Unable to lock the repository");

                //now that I have the test lock, it should fail if I try to get it again.
                Assert.Catch<LockTimeoutException>(() =>
                        {
                            using (var failedLock = lockManager.Lock(this, MultiprocessLockName, 0))
                            {
                                Assert.IsNull(failedLock, "Duplicate lock was allowed.");
                            }
                        });
            }
        }

        [Test]
        public void Can_Acquire_Different_Lock_In_Same_Scope()
        {
            var lockManager = new DistributedLockManager(GetLockProvider(DefaultLockDatabase));

            // Now test other scenarios while another thread holds the lock.
            using (var otherLock = OtherThreadLockHelper.TryLock(this, lockManager, MultiprocessLockName + "_alternate", 0))
            {
                Assert.IsNotNull(otherLock, "Unable to establish first lock in scope.");

                using (var testLock = lockManager.Lock(this, MultiprocessLockName, 0))
                {
                    Assert.IsNotNull(testLock, "Unable to establish second lock in scope.");
                }
            }
        }

        [Test]
        public void Can_Acquire_Same_Lock_In_Different_Scope()
        {
            var firstLockManager = new DistributedLockManager(GetLockProvider(DefaultLockDatabase));

            // Now test other scenarios while another thread holds the lock.
            using (var testLock = firstLockManager.Lock(this, MultiprocessLockName, 0))
            {
                Assert.IsNotNull(testLock, "Unable to establish lock on first scope");

                var secondLockManager = new DistributedLockManager(GetLockProvider(SecondLockDatabase));
                using (var secondTestLock = secondLockManager.Lock(this, MultiprocessLockName, 0))
                {
                    Assert.IsNotNull(secondTestLock, "Unable to establish lock on second scope.");

                    var thirdLockManager = new DistributedLockManager(GetLockProvider(ThirdLockDatabase));
                    using (var thirdTestLock = thirdLockManager.Lock(this, MultiprocessLockName, 0))
                    {
                        Assert.IsNotNull(thirdTestLock, "Unable to establish lock on third scope.");
                    }
                }
            }
        }

        [Test]
        public void LockRepositoryTimeout()
        {
            var lockManager = new DistributedLockManager(GetLockProvider(DefaultLockDatabase));

            using (var testLock = OtherThreadLockHelper.TryLock(this, lockManager, MultiprocessLockName, 0))
            {
                Assert.IsNotNull(testLock, "Unable to lock the repository");

                //now when we try to get it we should not, and should wait at least our timeout
                var lockStart = DateTimeOffset.Now;
                DistributedLock timeoutLock;
                Assert.IsFalse(lockManager.TryLock(this, MultiprocessLockName, 5, out timeoutLock));
                using (timeoutLock)
                {
                    //we shouldn't have the lock
                    Assert.IsNull(timeoutLock, "Duplicate lock allowed");

                    //and we should be within a reasonable delta of our timeout.
                    var delay = DateTimeOffset.Now - lockStart;
                    Trace.Write(string.Format("Repository Timeout Requested: {0} Actual: {1}", 5, delay.TotalSeconds));
                    Assert.Greater(delay.TotalSeconds, 4.5, "Timeout happened too fast - {0} seconds", delay.TotalSeconds);
                    Assert.Less(delay.TotalSeconds, 5.5, "Timeout happened too slow - {0} seconds", delay.TotalSeconds);
                }
            }
        }

        [Test]
        public void Can_Acquire_Lock_Many_Times()
        {
            var lockManager = new DistributedLockManager(GetLockProvider(DefaultLockDatabase));

            var lockIterations = 1000;

            for (var curIteration = 0; curIteration < lockIterations; curIteration++)
            {
                using (var outerLock = lockManager.Lock(this, MultiprocessLockName, 0))
                {
                    Assert.IsNotNull(outerLock, "Unable to acquire lock on iteration {0:N0}", curIteration);
                }
            }
        }

        [Test]
        public async Task Can_Acquire_Lock_Many_Times_Async()
        {
            var lockManager = new DistributedLockManager(GetLockProvider(DefaultLockDatabase));

            var lockIterations = 1000;

            for (var curIteration = 0; curIteration < lockIterations; curIteration++)
            {
                try
                {
                    var outerLock = lockManager.Lock(this, MultiprocessLockName, 0);

                    try
                    {
                        Assert.IsNotNull(outerLock, "Unable to acquire lock on iteration {0:N0}", curIteration);

                        //now we need to do something else async so we resume back.
                        await GratuitousWorkAsync(outerLock).ConfigureAwait(false);
                    }
                    finally
                    {
                        outerLock.Dispose();
                    }
                }
                catch (LockTimeoutException ex)
                {
                    throw new Exception("Unable to acquire the lock immediately on iteration " + curIteration, ex);
                }
            }
        }

        private async Task GratuitousWorkAsync(DistributedLock distributedLock)
        {
            Assert.That(distributedLock.IsDisposed == false);

            await Task.Delay(10).ConfigureAwait(false);
            await Task.Delay(10).ConfigureAwait(false);
            await Task.Delay(10).ConfigureAwait(false);
        }

        private SqlLockProvider GetLockProvider(string databaseName)
        {
            var connectionString = string.Format(ConnectionStringTemplate, "192.168.1.73", databaseName);

            return new SqlLockProvider(connectionString);
        }
    }
}
