// /*
//    IDistributedLockProvider.cs
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

namespace Gibraltar.DistributedLocking
{
    /// <summary>
    /// Creates and manages distributed locks
    /// </summary>
    public interface IDistributedLockProvider
    {
        /// <summary>
        /// A unique name for this lock provider and its scope
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Attempts to get an exclusive lock.
        /// </summary>
        /// <param name="name">The unique name of the lock.</param>
        /// <returns>A disposable object if locked, null otherwise</returns>
        /// <remarks>Callers should check the provided handle for null to ensure they got the lock on the file.
        /// If it is not null, it must be disposed to release the lock in a timely manner.</remarks>
        IDisposable GetLock(string name);

        /// <summary>
        /// Attempts to request a turn at an exclusive lock.
        /// </summary>
        /// <param name="name">The unique name of the lock.</param>
        /// <returns>A disposable object holding a lock request if available, null otherwise</returns>
        /// <remarks>Callers should check the provided handle for null to ensure they got a valid lock request on the file.
        /// If it is not null, it must be disposed to release the request when expired or full lock is acquired.</remarks>
        IDisposable GetLockRequest(string name);

        /// <summary>
        /// Check if a lock request is pending (without blocking).
        /// </summary>
        /// <param name="name">The unique name of the lock.</param>
        /// <returns>True if a lock request is pending, false otherwise.</returns>
        bool CheckLockRequest(string name);
    }
}
