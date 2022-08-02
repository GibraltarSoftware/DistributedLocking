#region File Header and License
// /*
//    DistributedLock.cs
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
using System.Threading;
using Gibraltar.DistributedLocking.Internal;

namespace Gibraltar.DistributedLocking
{
    /// <summary>
    /// Represents an exclusive distributed lock.
    /// </summary>
    /// <remarks>To be valid, the distributed lock object must be obtained from the <see cref="DistributedLockManager">DistributedLockManager class</see>.
    /// When you're done with this lock, dispose it to release it.</remarks>
    public sealed class DistributedLock : IDisposable
    {
        private readonly CancellationToken _cancellation;
        private readonly object _myLock = new object(); // For locking inter-thread signals to this instance.

        private Guid _owningLockId;
        private object _owningObject;
        private DistributedLockProxy _ourLockProxy;
        private DistributedLock _actualLock; // LOCKED by MyLock
        private bool _myTurn; // LOCKED by MyLock
        private bool _disposed; // LOCKED by MyLock

        /// <summary>
        /// Raised when the lock is disposed.
        /// </summary>
        internal event EventHandler Disposed;

        internal DistributedLock(object requester, string name, CancellationToken token)
        {
            _owningLockId = DistributedLockManager.CurrentLockId;
            _owningObject = requester;
            Name = name;
            _actualLock = null;
            _myTurn = false;
            _cancellation = token;
        }

        #region Public Properties and Methods

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
        /// The name of the lock within the repository.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The object that is currently holding the lock.
        /// </summary>
        public object Owner => _owningObject;

        /// <summary>
        /// The ManagedThreadId of the thread that owns this lock instance.
        /// </summary>
        public Guid OwningLockId => _owningLockId;

        /// <summary>
        /// Whether this lock request is willing to wait (finite) for the lock or return immediately if not available.
        /// </summary>
        public bool WaitForLock => _cancellation.CanBeCanceled;

        /// <summary>
        /// The actual holder of the lock if we are a secondary lock on the same thread, or ourselves if we hold the file lock.
        /// </summary>
        public DistributedLock ActualLock
        {
            get
            {
                lock(_myLock)
                {
                    Monitor.PulseAll(_myLock);
                    return _actualLock;
                }
            }
        }

        /// <summary>
        /// Reports if this lock object holds a secondary lock rather than the actual lock (or no lock).
        /// </summary>
        public bool IsSecondaryLock
        {
            get
            {
                lock(_myLock)
                {
                    Monitor.PulseAll(_myLock);
                    return _actualLock != null && (ReferenceEquals(_actualLock, this) == false);
                }
            }
        }

        /// <summary>
        /// Reports if this request instance has expired and should be skipped over because no thread is still waiting on it.
        /// </summary>
        public bool IsExpired => _cancellation.IsCancellationRequested;

        /// <summary>
        /// Whether this lock instance has been disposed (and thus does not hold any locks).
        /// </summary>
        public bool IsDisposed
        {
            get
            {
                lock(_myLock)
                {
                    Monitor.PulseAll(_myLock);
                    return _disposed;
                }
            }
        }

        /// <summary>
        /// Gets or sets the dispose-on-close policy for the lock proxy associated with this lock instance.
        /// </summary>
        public bool DisposeProxyOnClose
        {
            get
            {
                return (_ourLockProxy == null) ? false : _ourLockProxy.DisposeOnClose;
            }
            set
            {
                if (_ourLockProxy != null)
                    _ourLockProxy.DisposeOnClose = value;
            }
        }

        #endregion

        #region Internal Properties and Methods

        /// <summary>
        /// The proxy who will actually hold the file lock on our behalf.
        /// </summary>
        internal DistributedLockProxy OurLockProxy
        {
            get { return _ourLockProxy; }
            set { _ourLockProxy = value; }
        }

        internal void GrantTheLock(DistributedLock actualLock)
        {
            lock(_myLock)
            {
                try
                {
                    if (actualLock != null && actualLock.IsDisposed == false && actualLock.OwningLockId == _owningLockId &&
                        string.Equals(actualLock.Name, Name, StringComparison.OrdinalIgnoreCase))
                    {
                        // We don't need to lock around this because we're bypassing the proxy's queue and staying only on our own thread.
                        _actualLock = actualLock;
                    }
                    else
                    {
                        // It's an invalid call, so make sure our setting is cleared out.
                        _actualLock = null;
                        throw new InvalidOperationException("Can't set a secondary lock from an invalid actual lock.");
                    }
                }
                finally
                {
                    Monitor.PulseAll(_myLock);
                }
            }
        }

        internal void SignalMyTurn()
        {
            lock (_myLock)
            {
                _myTurn = true; // Flag it as being our turn.

                Monitor.PulseAll(_myLock); // And signal Monitor.Wait that we changed the state.
            }
        }

        internal bool AwaitTurnOrTimeout()
        {
            lock (_myLock)
            {
                try
                {
                    if (_cancellation.CanBeCanceled) // Never changes, so check it first.
                    {
                        while (_myTurn == false && _disposed == false) // Either flag and we're done waiting.
                        {
                            if (_cancellation.IsCancellationRequested)
                                return false; //we cancelled before we got the lock.

                            Monitor.Wait(_myLock, 10);
                        }
                    }

                    // Now we've done any allowed waiting as needed, check what our status is.
                    if (_disposed || _myTurn == false)
                        return false; // We're expired!
                    else
                        return true; // Otherwise, we're not disposed and it's our turn!
                }
                finally
                {
                    Monitor.PulseAll(_myLock);
                }
            }
        }

        #endregion

        #region Private Properties and Methods

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

                lock (_myLock)
                {
                    if (!_disposed)
                    {
                        _disposed = true; // Make sure we don't do it more than once.
                        _owningObject = null;
                    }

                    Monitor.PulseAll(_myLock); // No one should be waiting, but...
                }

                OnDispose(); // Fire whether it's first time or redundant.  Subscribers must sanity-check and can unsubscribe.
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

        #endregion
    }
}
