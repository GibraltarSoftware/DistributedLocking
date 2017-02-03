
using System;

namespace Gibraltar.DistributedLocking
{
    /// <summary>
    /// Thrown to indicate a failure to acquire the requested repository lock
    /// </summary>
    public class LockTimeoutException : LockException
    {
        /// <summary>
        /// Create a new exception instance for a lock timeout
        /// </summary>
        /// <param name="providerName">The name of the distributed lock provider</param>
        /// <param name="lockName">The name of the lock to get (locks are a combination of index and this name)</param>
        /// <param name="timeoutSeconds">The maximum number of seconds to wait on the lock before giving up.</param>
        /// <param name="message">The error message string.</param>
        public LockTimeoutException(string providerName, string lockName, int timeoutSeconds, string message)
            :this(providerName, lockName, timeoutSeconds, message, null)
        {
        }

        /// <summary>
        /// Create a new exception instance for a lock timeout
        /// </summary>
        /// <param name="providerName">The name of the distributed lock provider</param>
        /// <param name="lockName">The name of the lock to get (locks are a combination of index and this name)</param>
        /// <param name="timeoutSeconds">The maximum number of seconds to wait on the lock before giving up.</param>
        /// <param name="message">The error message string.</param>
        /// <param name="innerException">The exception that is the cause of the current exception, or a
        /// null reference if no inner exception is specified.</param>
        public LockTimeoutException(string providerName, string lockName, int timeoutSeconds, string message, Exception innerException)
            : base(message, innerException)
        {
            ProviderName = providerName;
            LockName = lockName;
            TimeoutSeconds = timeoutSeconds;
        }

        /// <summary>
        /// The unique name for the lock provider
        /// </summary>
        public string ProviderName { get; }

        /// <summary>
        /// The number of seconds the lock waited before it timed out.
        /// </summary>
        public int TimeoutSeconds { get; }

        /// <summary>
        /// The name of the lock being acquired
        /// </summary>
        public string LockName { get; }

        /// <summary>
        /// Creates and returns a string representation of the current exception.
        /// </summary>
        /// <returns>
        /// A string representation of the current exception.
        /// </returns>
        /// <filterpriority>1</filterpriority><PermissionSet><IPermission class="System.Security.Permissions.FileIOPermission, mscorlib, Version=2.0.3600.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" version="1" PathDiscovery="*AllFiles*"/></PermissionSet>
        public override string ToString()
        {
            return string.Format("Unable to acquire the {0} lock in time after waiting {1} seconds.  Lock Provider: {2}", LockName, TimeoutSeconds, ProviderName);
        }
    }
}
