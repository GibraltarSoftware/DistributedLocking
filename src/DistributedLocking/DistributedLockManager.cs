#region File Header and License
// /*
//    DistributedLockManager.cs
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
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Gibraltar.DistributedLocking.Internal;

namespace Gibraltar.DistributedLocking
{
    /// <summary>
    /// A multiprocess lock manager for repositories
    /// </summary>
    /// <remarks>Manages locking first within the process and then extends the process lock to multiple processes
    /// by locking a file on disk.  Designed for use with the Using statement as opposed to the Lock statement.</remarks>
    public class DistributedLockManager
    {
        private readonly IDistributedLockProvider _provider;
        private readonly object _lock = new object();
        private readonly Dictionary<string, DistributedLockProxy> _proxies = new Dictionary<string, DistributedLockProxy>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Create a new distributed lock manager, denoting a scope of locks.
        /// </summary>
        /// <param name="provider"></param>
        public DistributedLockManager(IDistributedLockProvider provider)
        {
            _provider = provider;
        }

        /// <summary>
        /// A unique name for this lock manager and its scope
        /// </summary>
        public string Name => _provider.Name;

        /// <summary>
        /// Acquire a named lock
        /// </summary>
        /// <param name="requester">The object that is requesting the lock (useful for debugging purposes)</param>
        /// <param name="name">The name of the lock to get within the current scope</param>
        /// <param name="timeoutSeconds">The maximum number of seconds to wait on the lock before giving up.</param>
        /// <returns>A disposable Lock object if the lock was acquired.</returns>
        /// <exception cref="LockTimeoutException">Thrown if the lock can not be acquired within the timeout specified</exception>
        public DistributedLock Lock(object requester, string name, int timeoutSeconds)
        {
            DistributedLock grantedLock;
            if (TryLock(requester, name, timeoutSeconds, out grantedLock))
            {
                return grantedLock;
            }

            var message = (timeoutSeconds > 0) ? string.Format("Unable to acquire lock {0} within {1} seconds", name, timeoutSeconds)
                                 : string.Format("Unable to acquire lock {0} immediately", name);
            throw new LockTimeoutException(Name, name, timeoutSeconds, message);
        }

        /// <summary>
        /// Attempt to lock the repository with the provided index path.
        /// </summary>
        /// <param name="requester">The object that is requesting the lock (useful for debugging purposes)</param>
        /// <param name="name">The name of the lock to get within the current scope</param>
        /// <param name="timeoutSeconds">The maximum number of seconds to wait on the lock before giving up.</param>
        /// <param name="grantedLock">The disposable Lock object if the lock was acquired.</param>
        /// <returns>True if the lock was acquired or false if the lock timed out.</returns>
        public bool TryLock(object requester, string name, int timeoutSeconds, out DistributedLock grantedLock)
        {
            if (requester == null)
                throw new ArgumentNullException(nameof(requester));

            if (name == null)
                throw new ArgumentNullException(nameof(name));

            grantedLock = null;

            var candidateLock = new DistributedLock(requester, name, timeoutSeconds);

            // Lookup or create the proxy for the requested lock.
            DistributedLockProxy lockProxy;
            lock(_lock)
            {
                try
                {
                    if (_proxies.TryGetValue(candidateLock.Name, out lockProxy) == false)
                    {
                        // Didn't exist, need to make one.
                        lockProxy = new DistributedLockProxy(_provider, name);

                        lockProxy.Disposed += LockProxy_Disposed;
                        _proxies.Add(lockProxy.Name, lockProxy);
                    }

                    // Does the current thread already hold the lock?  (If it was still waiting on it, we couldn't get here.)
                    Thread currentTurnThread = lockProxy.CheckCurrentTurnThread(candidateLock);
                    if (Thread.CurrentThread == currentTurnThread && candidateLock.ActualLock != null)
                    {
                        Debug.Write(string.Format("Existing Lock Already Acquired: {0}-{1}", Name, name));
                        grantedLock = candidateLock; // It's a secondary lock, so we don't need to queue it or wait.
                        return true;
                    }

                    // Or is the lock currently held by another thread that we don't want to wait for?
                    if (currentTurnThread != null && candidateLock.WaitForLock == false)
                    {
                        Debug.Write(string.Format("Unable to Acquire Lock (can't wait): {0}-{1}", Name, name));

                        candidateLock.Dispose(); // We don't want to wait for it, so don't bother queuing an expired request.
                        return false; // Just fail out.
                    }

                    lockProxy.QueueRequest(candidateLock); // Otherwise, queue the request inside the lock to keep the proxy around.
                }
                finally
                {
                    Monitor.Pulse(_lock); //to get whoever's waiting a kick in the pants.
                }
            }

            // Now we have the proxy and our request is queued.  Make sure some thread is trying to get the file lock.
            bool ourTurn = false; // Assume false.
            try
            {
                ourTurn = lockProxy.AwaitOurTurnOrTimeout(candidateLock);
            }
            finally
            {
                if (ourTurn == false)
                {
                    // We have to make sure this gets disposed if we didn't get the lock, even if a ThreadAbortException occurs.
                    candidateLock.Dispose(); // Bummer, we didn't get it.  Probably already disposed, but safe to do again.
                    candidateLock = null; // Clear it out to report the failure.
                }
            }

            Debug.Write(string.Format(candidateLock == null ? "Unable to Acquire Lock: {0}-{1}" : "Lock Acquired: {0}-{1}", Name, name));
            grantedLock = candidateLock;
            return (grantedLock != null);
        }

        #region Event Handlers

        private void LockProxy_Disposed(object sender, EventArgs e)
        {
            DistributedLockProxy disposingProxy = (DistributedLockProxy)sender;

            lock (_lock)
            {
                string lockKey = disposingProxy.Name;
                DistributedLockProxy actualProxy;
                // Only remove the proxy if the one we're disposing is the one in our collection for that key.
                if (_proxies.TryGetValue(lockKey, out actualProxy) && ReferenceEquals(actualProxy, disposingProxy))
                {
                    _proxies.Remove(lockKey);
                }

                Monitor.PulseAll(_lock);
            }
        }

        #endregion
    }
}