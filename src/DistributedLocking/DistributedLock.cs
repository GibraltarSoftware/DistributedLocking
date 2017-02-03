
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
        private readonly bool _waitForLock;
        private readonly object _myLock = new object(); // For locking inter-thread signals to this instance.

        private Thread _owningThread;
        private object _owningObject;
        private DistributedLockProxy _ourLockProxy;
        private DistributedLock _actualLock; // LOCKED by MyLock
        private DateTimeOffset _waitTimeout; // LOCKED by MyLock
        private bool _myTurn; // LOCKED by MyLock
        private bool _disposed; // LOCKED by MyLock

        /// <summary>
        /// Raised when the lock is disposed.
        /// </summary>
        internal event EventHandler Disposed;

        internal DistributedLock(object requester, string name, int timeoutSeconds)
        {
            _owningObject = requester;
            _owningThread = Thread.CurrentThread;
            Name = name;
            _actualLock = null;
            _myTurn = false;
            _waitForLock = (timeoutSeconds > 0);
            _waitTimeout = _waitForLock ? DateTimeOffset.Now.AddSeconds(timeoutSeconds) : DateTimeOffset.Now;
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
        /// The thread that created and waits on this request and owns the lock when this request is granted.
        /// </summary>
        public Thread OwningThread => _owningThread;

        /// <summary>
        /// The ManagedThreadId of the thread that owns this lock instance.
        /// </summary>
        public int OwningThreadId => _owningThread.ManagedThreadId;

        /// <summary>
        /// Whether this lock request is willing to wait (finite) for the lock or return immediately if not available.
        /// </summary>
        public bool WaitForLock => _waitForLock;

        /// <summary>
        /// The clock time at which this lock request wants to stop waiting for the lock and give up.
        /// (MaxValue once the lock is granted, MinValue if the lock was denied.)
        /// </summary>
        public DateTimeOffset WaitTimeout
        {
            get
            {
                lock(_myLock)
                {
                    Monitor.PulseAll(_myLock);
                    return _waitTimeout;
                }
            }
        }

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
        public bool IsExpired
        {
            get
            {
                lock (_myLock)
                {
                    Monitor.PulseAll(_myLock);
                    return _disposed || _waitTimeout == DateTimeOffset.MinValue;
                }
            }
        }

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
                if (actualLock != null && actualLock.IsDisposed == false && actualLock.OwningThread == _owningThread &&
                    string.Equals(actualLock.Name, Name, StringComparison.OrdinalIgnoreCase))
                {
                    // We don't need to lock around this because we're bypassing the proxy's queue and staying only on our own thread.
                    _actualLock = actualLock;
                    _waitTimeout = DateTimeOffset.MaxValue; // We have a lock (sort of), so reset our timeout to forever.
                }
                else
                {
                    // It's an invalid call, so make sure our setting is cleared out.
                    _actualLock = null;
                    throw new InvalidOperationException("Can't set a secondary lock from an invalid actual lock.");
                }

                Monitor.PulseAll(_myLock);
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
                if (_waitForLock) // Never changes, so check it first.
                {
                    while (_myTurn == false && _disposed == false) // Either flag and we're done waiting.
                    {
                        var howLong = _waitTimeout - DateTimeOffset.Now;
                        if (howLong.TotalMilliseconds <= 0)
                        {
                            _waitTimeout = DateTimeOffset.MinValue; // Mark timeout as expired.
                            return false; // Our time is up!
                        }

                        // We don't need to do a pulse here, we're the only ones waiting, and we didn't change any state.
                        Monitor.Wait(_myLock, howLong);
                    }
                }

                // Now we've done any allowed waiting as needed, check what our status is.

                if (_disposed || _myTurn == false)
                    return false; // We're expired!
                else
                    return true; // Otherwise, we're not disposed and it's our turn!

                // We don't need to do a pulse here, we're the only ones waiting, and we didn't change any state.
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
                        _waitTimeout = DateTimeOffset.MinValue;
                        _owningThread = null;
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
