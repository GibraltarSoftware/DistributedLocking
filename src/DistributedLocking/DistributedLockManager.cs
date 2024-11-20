#region File Header and License
// /*
//    DistributedLockManager.cs
//    Copyright 2008-2024 Gibraltar Software, Inc.
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
using System.Collections.Concurrent;
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
        private readonly ConcurrentDictionary<string, DistributedLockProxy> _proxies = new ConcurrentDictionary<string, DistributedLockProxy>(StringComparer.OrdinalIgnoreCase);

        private static readonly AsyncLocal<Guid?> LocalContext = new AsyncLocal<Guid?>();

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
            => Lock(requester, name, GetCancellationToken(timeoutSeconds));

        /// <summary>
        /// Acquire a named lock
        /// </summary>
        /// <param name="requester">The object that is requesting the lock (useful for debugging purposes)</param>
        /// <param name="name">The name of the lock to get within the current scope</param>
        /// <param name="token">Optional.  A cancellation token to limit how long to wait for the lock.</param>
        /// <returns>A disposable Lock object if the lock was acquired.</returns>
        /// <exception cref="LockTimeoutException">Thrown if the lock can not be acquired within the timeout specified</exception>
        public DistributedLock Lock(object requester, string name, CancellationToken token = default)
        {
            var requestStartTime = DateTime.UtcNow;
            if (TryLock(requester, name, token, out var grantedLock))
            {
                return grantedLock;
            }
            
            var waitDuration = DateTime.UtcNow - requestStartTime;

            var message = (token == CancellationToken.None) ? string.Format("Unable to acquire lock {0} immediately", name)
            : string.Format("Unable to acquire lock {0} within {1}", name, waitDuration);
            throw new LockTimeoutException(Name, name, waitDuration, message);
        }

        /// <summary>
        /// The unique Id for the current execution context
        /// </summary>
        public static Guid CurrentLockId
        {
            get
            {
                var contextId = LocalContext.Value;

                if (contextId == null)
                {
                    LocalContext.Value = contextId = Guid.NewGuid();
                }

                return contextId.Value;
            }
        }

        /// <summary>
        /// Called after starting a new async operation to prevent locks from inheriting to it.
        /// </summary>
        public static void LockBarrier()
        {
            LocalContext.Value = Guid.NewGuid();
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
         => TryLock(requester, name, GetCancellationToken(timeoutSeconds), out grantedLock);


        /// <summary>
        /// Attempt to lock the repository with the provided index path.
        /// </summary>
        /// <param name="requester">The object that is requesting the lock (useful for debugging purposes)</param>
        /// <param name="name">The name of the lock to get within the current scope</param>
        /// <param name="token">The cancellation token to stop waiting for the lock.</param>
        /// <param name="grantedLock">The disposable Lock object if the lock was acquired.</param>
        /// <returns>True if the lock was acquired or false if the lock timed out.</returns>
        public bool TryLock(object requester, string name, CancellationToken token, out DistributedLock grantedLock)
        {
            if (requester == null)
                throw new ArgumentNullException(nameof(requester));

            if (name == null)
                throw new ArgumentNullException(nameof(name));

            grantedLock = null;

            var candidateLock = new DistributedLock(requester, name, token);

            // Lookup or create the proxy for the requested lock.
            var lockProxy = _proxies.GetOrAdd(candidateLock.Name, (key) =>
            {
                var newProxy = new DistributedLockProxy(_provider, key);

                newProxy.Disposed += LockProxy_Disposed;
                return newProxy;
            });

            lock (lockProxy)
            {
                try
                {
                    // Does the current thread already hold the lock?  (If it was still waiting on it, we couldn't get here.)
                    var currentTurnLockId = lockProxy.CheckCurrentTurnThread(candidateLock);

                    if (currentTurnLockId != null && DistributedLockManager.CurrentLockId == currentTurnLockId && candidateLock.ActualLock != null)
                    {
                        Debug.Write(string.Format("Existing Lock Already Acquired: {0}-{1}", Name, name));
                        grantedLock = candidateLock; // It's a secondary lock, so we don't need to queue it or wait.
                        return true;
                    }

                    // Or is the lock currently held by another thread that we don't want to wait for?
                    if (currentTurnLockId != null && candidateLock.WaitForLock == false)
                    {
                        Debug.Write(string.Format("Unable to Acquire Lock (can't wait): {0}-{1}", Name, name));

                        candidateLock.Dispose(); // We don't want to wait for it, so don't bother queuing an expired request.
                        return false; // Just fail out.
                    }

                    lockProxy.QueueRequest(candidateLock); // Otherwise, queue the request inside the lock to keep the proxy around.
                }
                finally
                {
                    Monitor.Pulse(lockProxy); //to get whoever's waiting a kick in the pants.
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

        private CancellationToken GetCancellationToken(int timeoutSeconds)
        {
            //If they specified zero or less for seconds we presume they won't wait and want to immediately timeout.
            //If greater then use that number of seconds.
            return timeoutSeconds <= 0 ? CancellationToken.None : new CancellationTokenSource(new TimeSpan(0, 0, timeoutSeconds)).Token;
        }

        #region Event Handlers

        private void LockProxy_Disposed(object sender, EventArgs e)
        {
            var disposingProxy = (DistributedLockProxy)sender;

            var lockKey = disposingProxy.Name;

            // Only remove the proxy if the one we're disposing is the one in our collection for that key.
            DistributedLockProxy actualProxy;
            if (_proxies.TryRemove(lockKey, out actualProxy))
            {
                if (ReferenceEquals(actualProxy, disposingProxy))
                {
                    //good, the object we got is us so we are the one true proxy.
                    disposingProxy.Disposed -= LockProxy_Disposed;
                }
                else
                {
                    //ruh roh; it wasn't us - we need to put that back in.
                    DistributedLockProxy errantProxy = null;
                    Debug.Write(string.Format("Lock proxy for lock {0} is not our object, re-inserting", lockKey));
                    _proxies.AddOrUpdate(lockKey, actualProxy, (key, proxy) =>
                                                               {
                                                                   errantProxy = proxy;
                                                                   return actualProxy;
                                                               });

                    //we really should merge proxies..
                    errantProxy.Dispose();
                }
            }
            else
            {
                //we're somehow a dangling proxy; still release our delegate reference so we don't leak.
                Debug.Write(string.Format("Lock proxy for lock {0} was not in the proxies dictionary.", lockKey));
                disposingProxy.Disposed -= LockProxy_Disposed;
            }
        }

        #endregion
    }
}