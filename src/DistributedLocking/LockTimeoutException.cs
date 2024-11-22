// /*
//    LockTimeoutException.cs
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
using System.Diagnostics;

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
        /// <param name="timeout">The duration the lock waited to timeout.</param>
        /// <param name="message">The error message string.</param>
        public LockTimeoutException(string providerName, string lockName, TimeSpan timeout, string message)
            :this(providerName, lockName, timeout, message, null)
        {
        }

        /// <summary>
        /// Create a new exception instance for a lock timeout
        /// </summary>
        /// <param name="providerName">The name of the distributed lock provider</param>
        /// <param name="lockName">The name of the lock to get (locks are a combination of index and this name)</param>
        /// <param name="timeout">The duration the lock waited to timeout.</param>
        /// <param name="message">The error message string.</param>
        /// <param name="innerException">The exception that is the cause of the current exception, or a
        /// null reference if no inner exception is specified.</param>
        public LockTimeoutException(string providerName, string lockName, TimeSpan timeout, string message, Exception innerException)
            : base(message, innerException)
        {
            ProviderName = providerName;
            LockName = lockName;
            Timeout = timeout;
            TimeoutSeconds = Convert.ToInt32(Timeout.TotalSeconds);

            if (Debugger.IsAttached)
                Debugger.Break();
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
        /// The duration the lock waited before it timed out.
        /// </summary>
        public TimeSpan Timeout { get; }

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
