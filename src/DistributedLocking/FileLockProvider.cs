#region File Header and License
// /*
//    FileLockProvider.cs
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
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using Gibraltar.DistributedLocking.Internal;
using Microsoft.Win32.SafeHandles;

namespace Gibraltar.DistributedLocking
{
    /// <summary>
    /// Provide distributed locks via files in a network path
    /// </summary>
    [DebuggerDisplay("Path: {Name}")]
    public class FileLockProvider : IDistributedLockProvider
    {
        /// <summary>
        /// The file extension of lock files used to lock repositories..
        /// </summary>
        private const string LockFileExtension = "lock";

        private readonly string _path;
        private readonly bool _deleteOnClose;

        /// <summary>
        /// Create a file lock provider scoped to the provided path.
        /// </summary>
        /// <param name="path">The network path which determines the scope of locks</param>
        /// <param name="deleteOnClose">True to clean up (delete) lock files as they are closed.</param>
        public FileLockProvider(string path, bool deleteOnClose = true)
        {
            _path = path;
            Name = _path.ToLowerInvariant(); //to ensure comparability.
            _deleteOnClose = deleteOnClose;
        }

        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        public IDisposable GetLock(string name)
        {
            FileLock fileLock = null;

            try
            {
                Directory.CreateDirectory(_path);
            }
            catch (UnauthorizedAccessException ex)
            {
                GC.KeepAlive(ex);
#if DEBUG
                Trace.TraceWarning("Unable to create directory to index path, locking will not be feasible.  Exception: {0}", ex);
#endif
                throw; // we aren't going to try to spinlock on this.. we failed.
            }
            catch (Exception ex)
            {
                GC.KeepAlive(ex);
#if DEBUG
                Trace.TraceWarning("Unable to create directory to index path, locking will not be feasible.  Exception: {0}", ex);
#endif
            }

            var lockFullFileNamePath = GetLockFileName(_path, name);

            try
            {
                // We share Read so that other processes who desire the lock can open for read to signal that to us.
                // (Except on Mono we have to use a separate file for the request signal, but this is still okay for the lock.)
                fileLock = OpenFileAccess(lockFullFileNamePath, FileAccess.Write, FileShare.Read, _deleteOnClose);
            }
            catch (ThreadAbortException)
            {
                if (fileLock != null)
                    fileLock.Dispose(); // Make sure it's cleaned up if the requesting thread is aborting!

                throw;
            }
            catch
            {
                //don't care why we failed, we just did - so no lock for you!
                fileLock = null;
            }

            return fileLock;
        }

        /// <inheritdoc />
        public IDisposable GetLockRequest(string name)
        {
            FileLock fileLock;

            try
            {
                Directory.CreateDirectory(_path);
            }
            catch (Exception ex)
            {
                GC.KeepAlive(ex);
#if DEBUG
                Trace.TraceWarning("Unable to create directory to index path, locking will not be feasible.  Exception: {0}", ex);
#endif
            }

            var lockFullFileNamePath = GetLockFileName(_path, name);

            // We share ReadWrite so that we overlap with an open lock (unshared write) and other requests (open reads).
            FileShare fileShare = FileShare.ReadWrite;

            if (CommonCentralLogic.IsMonoRuntime) // ...except on Mono that doesn't work properly.
            {
                lockFullFileNamePath += "req"; // We must use a separate file for lock requests on Mono.
                fileShare = FileShare.Read; // Allow any other reads, but not writes.
            }

            try
            {
                // This is meant to overlap with other requestors, so it should never delete on close; others may still have it open.
                fileLock = OpenFileAccess(lockFullFileNamePath, FileAccess.Read, fileShare, false);
            }
            catch
            {
                // We don't care why we failed, we just did - so no lock for you!
                fileLock = null;
            }

            return fileLock;
        }

        /// <inheritdoc />
        public bool CheckLockRequest(string name)
        {
            try
            {
                Directory.CreateDirectory(_path);
            }
            catch (Exception ex)
            {
                GC.KeepAlive(ex);
#if DEBUG
                Trace.TraceWarning("Unable to create directory to index path, locking will not be feasible.  Exception: {0}", ex);
#endif
            }

            var lockFullFileNamePath = GetLockFileName(_path, name);

            // We share Write because we'll check this while we already have an unshared write open!
            FileAccess fileAccess = FileAccess.Read;
            FileShare fileShare = FileShare.Write;
            bool deleteOnClose = false; // This overlaps with holding a write lock, so don't delete the file when successful.

            if (CommonCentralLogic.IsMonoRuntime) // ...except on Mono that doesn't work properly.
            {
                lockFullFileNamePath += "req"; // We use a separate file for lock requests on Mono.
                fileAccess = FileAccess.Write; // Writes would be blocked by a request sharing only other reads.
                fileShare = FileShare.None; // This would probably be okay to share, but doesn't hurt to err as cautious.
                deleteOnClose = _deleteOnClose; // Separate file needs to be cleaned up, should we do it after success below?
                if (File.Exists(lockFullFileNamePath) == false)
                    return false; // Don't bother creating the request file if it doesn't exist:  No one can be waiting.
            }

            try
            {
                using (FileLock fileLockRequest = OpenFileAccess(lockFullFileNamePath, fileAccess, fileShare, deleteOnClose))
                {
                    return (fileLockRequest == null); // There's an open read on it if we could NOT open an unshared read.
                }
            }
            catch
            {
                // We don't care why we failed, we just did - so assume there IS a request pending.
                return true;
            }
        }

        /// <summary>
        /// Open a file for the specified fileAccess and fileShare, or return null if open fails (avoids exceptions).
        /// </summary>
        /// <param name="fullFileNamePath">The full-path file name to open for the specified access.</param>
        /// <param name="fileAccess">The FileAccess with which to open the file.</param>
        /// <param name="fileShare">The FileShare to allow to overlap with this open.</param>
        /// <param name="manualDeleteOnClose">Whether the (successfully-opened) FileLock returned should delete the file
        /// upon dispose.</param>
        /// <returns>A disposable FileLock opened with the specified access and sharing), or null if the attempt failed.</returns>
        private FileLock OpenFileAccess(string fullFileNamePath, FileAccess fileAccess, FileShare fileShare, bool manualDeleteOnClose)
        {
            uint flags = 0;

            FileLock fileOpen = null;
            if (CommonCentralLogic.IsMonoRuntime)
            {
                // We can't use P/Invoke, so create the file the direct .NET way.
                FileStream fileStream = null;
                try
                {
                    RuntimeHelpers.PrepareConstrainedRegions(); // Make sure we don't thread-abort in the FileStream() ctor.
                    try
                    {
                    }
                    finally
                    {
                        fileStream = new FileStream(fullFileNamePath, FileMode.OpenOrCreate, fileAccess, fileShare,
                                                    8192, (FileOptions)flags);
                    }
                }
                catch (ThreadAbortException)
                {
                    if (fileStream != null)
                        fileStream.Dispose(); // Make sure this gets cleaned up if the requesting thread is aborting!

                    throw; // Does this help preserve stack trace info?
                }
                catch
                {
                    fileStream = null;
                }

                if (fileStream != null)
                    fileOpen = new FileLock(fileStream, fullFileNamePath, FileMode.OpenOrCreate, fileAccess, fileShare, manualDeleteOnClose);
            }
            else
            {
                SafeFileHandle fileHandle = null;
                try
                {
                    RuntimeHelpers.PrepareConstrainedRegions(); // Make sure we don't thread-abort in the CreateFile() call.
                    try
                    {
                    }
                    finally
                    {
                        // We can use P/Invoke to avoid having to catch an exception for failure.
                        fileHandle = Win32Helper.CreateFile(fullFileNamePath, fileAccess, fileShare,
                                                            IntPtr.Zero, FileMode.OpenOrCreate, flags, IntPtr.Zero);
                    }

                    if (fileHandle != null && (fileHandle.IsInvalid || fileHandle.IsClosed))
                    {
                        // We couldn't open it properly, so we don't need the file handle.
                        fileHandle.Dispose(); // just so we're absolutely clear.

                        fileHandle = null;
                    }

                    if (fileHandle != null)
                        fileOpen = new FileLock(fileHandle, fullFileNamePath, FileMode.OpenOrCreate, fileAccess, fileShare, manualDeleteOnClose);
                }
                catch (ThreadAbortException)
                {
                    if (fileOpen != null)
                        fileOpen.Dispose(); // This will also dispose the fileHandle for us.
                    else if (fileHandle != null)
                        fileHandle.Dispose(); // Make sure this gets cleaned up if the requesting thread is aborting!

                    throw; // Does this help preserve stack trace info?
                }
            }

            return fileOpen;
        }

        private static string GetLockFileName(string indexPath, string lockName)
        {
            //we have to sanitize the lock name to a safe file name.
            var adjustedLockName = string.Join("_", lockName.Split(Path.GetInvalidFileNameChars()));

            if (lockName.Equals(adjustedLockName, StringComparison.OrdinalIgnoreCase) == false)
            {
                //since we did redact it we may have dropped a key variable between two locks, so we need
                //to do something to restore the correct uniqueness.
                adjustedLockName += lockName.GetHashCode();
            }

            return Path.Combine(indexPath, adjustedLockName + "." + LockFileExtension);
        }
    }
}
