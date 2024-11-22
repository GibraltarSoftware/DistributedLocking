// /*
//    DistributedLockProxy.cs
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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Gibraltar.DistributedLocking.Internal
{
    /// <summary>
    /// A class to hold a lock for this process (app domain) and pass it fairly to other waiting threads before release.
    /// </summary>
    internal class DistributedLockProxy
    {
        private const int LockPollingDelay = 16; // 16 ms wait between attempts to open a lock file.
        private const int BackOffDelay = LockPollingDelay * 3; // 48 ms wait when another process requests a turn.

        private readonly ConcurrentQueue<DistributedLock> _waitQueue = new ConcurrentQueue<DistributedLock>();
        private readonly object _currentLockLock = new object(); 
        private readonly IDistributedLockProvider _provider;
        private readonly string _name;

        private DistributedLock _currentLockTurn; //protected by currentLockLock
        private IDisposable _lock;//protected by currentLockLock
        private IDisposable _lockRequest;//protected by currentLockLock
        private DateTimeOffset _minTimeNextTurn = DateTimeOffset.MinValue;
        private bool _disposed;

        /// <summary>
        /// Raised when the lock is disposed.
        /// </summary>
        internal event EventHandler Disposed;

        internal DistributedLockProxy(IDistributedLockProvider provider, string name)
        {
            _provider = provider;
            _name = name;
        }

        ///<summary>
        ///Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        ///</summary>
        ///<filterpriority>2</filterpriority>
        public void Dispose()
        {
            // Call the underlying implementation
            Dispose(true);

            // SuppressFinalize because there won't be anything left to finalize
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// The name of the lock within the scope.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Whether this lock instance has been disposed (and thus does not hold any locks).
        /// </summary>
        public bool IsDisposed => _disposed;

        /// <summary>
        /// Reports how many threads are in the queue waiting on the lock (some may have timed out and given up already).
        /// (Reports -1 if the proxy is idle (no current turn).)
        /// </summary>
        public int WaitingCount => (_currentLockTurn == null) ? -1 : _waitQueue.Count;

        /// <summary>
        /// Object persistence policy for this instance:  Whether to dispose this instance when file lock is released.
        /// </summary>
        internal bool DisposeOnClose { get; set; }

        /// <summary>
        /// Check the thread with the current turn for the lock and grant a secondary lock if applicable.
        /// </summary>
        /// <param name="candidateLock">An unexpired lock request on the current thread, or null to just check the turn thread.</param>
        /// <returns>The activity Id with the current turn for the lock, or null if there are none holding or waiting.</returns>
        internal Guid? CheckCurrentTurnThread(DistributedLock candidateLock)
        {
            var currentLockId = DistributedLockManager.CurrentLockId;

            if (candidateLock != null && candidateLock.OwningLockId != currentLockId)
                throw new InvalidOperationException("A lock request may only be waited on by the thread which created it.");

            lock (_currentLockLock)
            {
                if (_currentLockTurn != null)
                {
                    var currentOwningLockId = _currentLockTurn.OwningLockId;
                    if (candidateLock != null && currentLockId == currentOwningLockId)
                    {
                        candidateLock.GrantTheLock(_currentLockTurn); // Set it as a secondary lock on that holder (same thread).
                        if (candidateLock.ActualLock == _currentLockTurn) // Sanity-check that it was successful.
                            candidateLock.OurLockProxy = this; // So its dispose-on-close setting pass-through can function.
                    }

                    return currentOwningLockId; // Whether it's a match or some other thread.
                }

                return null; // No thread owns the lock.
            }
        }

        /// <summary>
        /// Queue a lock request (RepositoryLock instance).  Must be followed by a call to AwaitOurTurnOrTimeout (which can block).
        /// </summary>
        /// <param name="lockRequest"></param>
        internal void QueueRequest(DistributedLock lockRequest)
        {
            if (string.Equals(lockRequest.Name, _name, StringComparison.OrdinalIgnoreCase) == false)
                throw new InvalidOperationException("A lock request may not be queued to a proxy for a different full name.");

            if (lockRequest.OwningLockId != DistributedLockManager.CurrentLockId)
                throw new InvalidOperationException("A lock request may only be queued by the thread which created it.");

            _waitQueue.Enqueue(lockRequest);
        }

        /// <summary>
        /// Wait for our turn to have the lock (and wait for the lock) up to our time limit
        /// </summary>
        /// <param name="lockRequest"></param>
        /// <returns></returns>
        internal bool AwaitOurTurnOrTimeout(DistributedLock lockRequest)
        {
            if (lockRequest.IsExpired)
                throw new InvalidOperationException("Can't wait on an expired lock request.");

            if (string.Equals(lockRequest.Name, _name, StringComparison.OrdinalIgnoreCase) == false)
                throw new InvalidOperationException("A lock request may not be queued to a proxy for a different full name.");

            if (lockRequest.OwningLockId != DistributedLockManager.CurrentLockId)
                throw new InvalidOperationException("A lock request may only be waited on by the thread which created it.");

            lockRequest.OurLockProxy = this; // Mark the request as pending with us.

            // Do NOT clear out current lock owner, this will allow DequeueNextRequest to find one already there, if any.
            bool ourTurn = StartNextTurn(lockRequest); // Gets its own queue lock.
            if (ourTurn == false)
            {
                // It's not our turn yet, we need to wait our turn.  Are we willing to wait?
                if (lockRequest.WaitForLock && lockRequest.IsExpired == false)
                    ourTurn = lockRequest.AwaitTurnOrTimeout();

                // Still not our turn?
                if (ourTurn == false)
                {
#if DEBUG
                    // Who actually has the lock right now?
                    lock(_currentLockLock)
                    {
                        if (_currentLockTurn != null)
                        {
                            var currentOwningActivityId = _currentLockTurn.OwningLockId;

                            Trace.WriteLine(string.Format("{0}\r\nA lock request gave up because it is still being held by another thread.\r\n" +
                                                            "Lock file: {1}\r\nCurrent holding Activity: {2}",
                                lockRequest.WaitForLock ? "Lock request timed out" : "Lock request couldn't wait",
                                _name, currentOwningActivityId));
                        }
                        else
                        {
                            Trace.TraceError("Lock request turn error\r\nA lock request failed to get its turn but the current lock turn is null.  " +
                                                "This probably should not happen.\r\nLock file: {0}\r\n", _name);
                        }
                    }
#endif

                    lockRequest.Dispose(); // Expire the request.
                    return false; // Failed to get the lock.  Time to give up.
                }
            }

            // Yay, now it's our turn!  Do we already hold the lock?

            bool validLock;

            IDisposable curLock;
            lock(_currentLockLock)
            {
                curLock = _lock;
            }

            if (curLock != null)
                validLock = true; // It's our request's turn and this proxy already holds the lock!
            else
                validLock = TryGetLock(lockRequest); // Can we get the lock?

            // Do we actually have the lock now?
            if (validLock)
            {
                lockRequest.GrantTheLock(lockRequest); // It owns the actual lock itself now.
            }
            else
            {
#if DEBUG
                Trace.WriteLine(string.Format("{0}\r\nA lock request gave up because it could not obtain the file lock.  " +
                                                "It is most likely still held by another process.\r\nLock file: {1}",
                                                lockRequest.WaitForLock ? "Lock request timed out" : "Lock request couldn't wait",
                                                _name));
#endif
                lockRequest.Dispose(); // Failed to get the lock.  Expire the request and give up.
            }

            return validLock;
        }

        /// <summary>
        /// Try to get the actual file lock on behalf of the current request.
        /// </summary>
        /// <param name="currentRequest"></param>
        /// <returns></returns>
        private bool TryGetLock(DistributedLock currentRequest)
        {
            var waitForLock = currentRequest.WaitForLock;
            var validLock = false;

            while (waitForLock == false || currentRequest.IsExpired == false)
            {
                if (DateTimeOffset.Now >= _minTimeNextTurn) // Make sure we aren't in a back-off delay.
                {
                    var newLock = _provider.GetLock(_name);
                    if (newLock != null)
                    {
                        lock(_currentLockLock)
                        {
                            _lock = newLock;

                            // We have the lock!  Close our lock request if we have one so later we can detect if anyone else does.
                            if (_lockRequest != null)
                            {
                                _lockRequest.Dispose();
                                _lockRequest = null;
                            }
                        }

                        validLock = true; // Report that we have the lock now.
                    }
                }
                // Otherwise, just pretend we couldn't get the lock in this attempt.

                if (validLock == false && waitForLock)
                {
                    // We didn't get the lock and we want to wait for it, so try to open a lock request.
                    lock(_currentLockLock)
                    {
                        if (_lockRequest == null)
                            _lockRequest = _provider.GetLockRequest(_name); // Tell the other process we'd like a turn.
                    }

                    // Then we should allow some real time to pass before trying again because external locks aren't very fast.
                    Thread.Sleep(LockPollingDelay);
                }
                else
                {
                    // We either got the lock or the user doesn't want to keep retrying, so exit the loop.
                    break;
                }
            }

            return validLock;
        }

        /// <summary>
        /// Find the next request still waiting and signal it to go.  Or return true if the current caller may proceed.
        /// </summary>
        /// <param name="currentRequest">The request the caller is waiting on, or null for none.</param>
        /// <returns>True if the caller's supplied request is the next turn, false otherwise.</returns>
        private bool StartNextTurn(DistributedLock currentRequest)
        {
            lock (_currentLockLock)
            {
                int dequeueCount = DequeueNextRequest(); // Find the next turn if there isn't one already underway.
                if (_currentLockTurn != null)
                {
                    // If we popped a new turn off the queue make sure it gets started.
                    if (dequeueCount > 0)
                        _currentLockTurn.SignalMyTurn(); // Signal the thread waiting on that request to proceed.

                    if (ReferenceEquals(_currentLockTurn, currentRequest)) // Is the current request the next turn?
                    {
                        return true; // Yes, so skip waiting and just tell our caller they can go ahead (and wait for the lock).
                    }
                }
                else
                {
                    // Otherwise, nothing else is waiting on the lock!  Time to shut it down.

                    if (_lockRequest != null)
                    {
                        _lockRequest.Dispose(); // Release the lock request (an open read) since we're no longer waiting on it.
                        _lockRequest = null;
                    }

                    if (_lock != null)
                    {
                        _lock.Dispose(); // Release the external lock.
                        _lock = null;
                    }

                    if (DisposeOnClose)
                        Dispose();
                }

                return false;
            }
        }

        private int DequeueNextRequest()
        {
            lock (_currentLockLock)
            {
                var dequeueCount = 0;
                RuntimeHelpers.PrepareConstrainedRegions(); // Make sure we don't thread-abort in the middle of this logic.
                try
                {
                }
                finally
                {
                    while (_currentLockTurn == null && _waitQueue.TryDequeue(out _currentLockTurn) == true)
                    {
                        dequeueCount++;

                        if (_currentLockTurn.IsExpired)
                        {
                            _currentLockTurn.Dispose(); // There's no one waiting on that request, so just discard it.
                            _currentLockTurn = null; // Get the next one (if any) on next loop.
                        }
                        else
                        {
                            _currentLockTurn.Disposed += Lock_Disposed; // Subscribe to their Disposed event.  Now we care.
                        }
                    }
                }

                return dequeueCount;
            }
        }

        /// <summary>
        /// Performs the actual releasing of managed and unmanaged resources.
        /// Most usage should instead call Dispose(), which will call Dispose(true) for you
        /// and will suppress redundant finalization.
        /// </summary>
        /// <param name="releaseManaged">Indicates whether to release managed resources.
        /// This should only be called with true, except from the finalizer which should call Dispose(false).</param>
        private void Dispose(bool releaseManaged)
        {
            if (releaseManaged)
            {
                // Free managed resources here (normal Dispose() stuff, which should itself call Dispose(true))
                // Other objects may be referenced in this case

                if (!_disposed)
                {
                    _disposed = true; // Make sure we don't do it more than once.

                    // Empty our queue (although it should already be empty!).
                    while (_waitQueue.IsEmpty == false)
                    {
                        if (_waitQueue.TryDequeue(out var lockInstance))
                        {
                            lockInstance.SafeDispose();// Tell any threads still waiting that their request has expired.
                        }
                    }

                    lock(_currentLockLock)
                    {
                        if (_currentLockTurn == null)
                        {
                            // No thread is currently prepared to do this, so clear them here.
                            if (_lockRequest != null)
                            {
                                _lockRequest.Dispose();
                                _lockRequest = null;
                            }

                            if (_lock != null)
                            {
                                _lock.Dispose();
                                _lock = null;
                            }
                        }
                    }

                    // We're not fully disposed until the current lock owner gets disposed so we can release the lock.
                    // But fire the event to tell the RepositoryLockManager that we are no longer a valid proxy.
                    OnDispose();
                }
            }
            else
            {
                // Even in this case when we are in the finalizer We need to be sure we release any object handles we may still have.
                _lockRequest = null;
                _lock = null;
            }
        }

        private void OnDispose()
        {
            EventHandler tempEvent = Disposed;

            if (tempEvent != null)
            {
                tempEvent.Invoke(this, new EventArgs());
            }
        }

        private void Lock_Disposed(object sender, EventArgs e)
        {
            DistributedLock disposingLock = (DistributedLock)sender;
            disposingLock.Disposed -= Lock_Disposed; // Unsubscribe.

            //we need to remove this object from the lock collection
            lock (_currentLockLock)
            {
                // Only remove the lock if the one we're disposing is the original top-level lock for that key.
                if (_currentLockTurn == null || ReferenceEquals(_currentLockTurn, disposingLock) == false)
                    return; // Wasn't our current holder, so we don't care about it.

                _currentLockTurn = null; // It's disposed, no longer current owner.

                if (_disposed == false)
                {
                    // We're releasing the lock for this thread.  We need to check if any other process has a request pending.
                    // And if so, we need to force this process to wait a minimum delay, even if we don't have one waiting now.
                    if (_lock != null && _provider.CheckLockRequest(_name))
                    {
                        _minTimeNextTurn = DateTimeOffset.Now.AddMilliseconds(BackOffDelay); // Back off for a bit.
                        _lock.Dispose(); // We have to give up the OS lock because other processes need a chance.
                        _lock = null;
                    }

                    StartNextTurn(null); // Find and signal the next turn to go ahead (also handles all-done).
                }
                else
                {
                    // We're already disposed, so we'd better release the lock and request now if we still have them!
                    if (_lockRequest != null)
                    {
                        _lockRequest.Dispose();
                        _lockRequest = null;
                    }

                    if (_lock != null)
                    {
                        _lock.Dispose();
                        _lock = null;
                    }
                }
            }
        }
    }
}
