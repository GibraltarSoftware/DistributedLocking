// /*
//    FileLockProvider.cs
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
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Gibraltar.DistributedLocking.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#if NETSTANDARD || NET6_0_OR_GREATER
using System.Runtime.InteropServices;
#endif

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
        private readonly ILogger<FileLockProvider> _logger;
        private readonly bool _isWindows;

        /// <summary>
        /// Create a file lock provider scoped to the provided path.
        /// </summary>
        /// <param name="path">The file system path which determines the scope of locks</param>
        /// <param name="deleteOnClose">True to clean up (delete) lock files as they are closed.</param>
        /// <remarks>It's recommended to use deleteOnClose = true so lock files are automatically cleaned up.  Set to false
        /// in scenarios where file system permissions are highly restricted and the lock files have been pre-created (and can't be deleted).</remarks>
        public FileLockProvider(string path, bool deleteOnClose = true)
            :this(path, deleteOnClose, NullLoggerFactory.Instance.CreateLogger<FileLockProvider>())
        {
        }

        /// <summary>
        /// Create a file lock provider scoped to the provided path.
        /// </summary>
        /// <param name="path">The file system path which determines the scope of locks</param>
        /// <param name="logger">Logger to use for diagnostics</param>
        /// <remarks>The lock provider will clean up (delete) lock files as they are closed.</remarks>
        public FileLockProvider(string path, ILogger<FileLockProvider> logger)
            :this(path, true, logger)
        {
        }

        /// <summary>
        /// Create a file lock provider scoped to the provided path.
        /// </summary>
        /// <param name="path">The file system path which determines the scope of locks</param>
        /// <param name="deleteOnClose">True to clean up (delete) lock files as they are closed.</param>
        /// <param name="logger">Logger to use for diagnostics</param>
        /// <remarks>It's recommended to use deleteOnClose = true so lock files are automatically cleaned up.  Set to false
        /// in scenarios where file system permissions are highly restricted and the lock files have been pre-created (and can't be deleted).</remarks>
        public FileLockProvider(string path, bool deleteOnClose, ILogger<FileLockProvider> logger)
        {
            _path = path;
            Name = _path.ToLowerInvariant(); //to ensure comparability.
            _deleteOnClose = deleteOnClose;
            _logger = logger;
#if NETFRAMEWORK
            _isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
#else
            _isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
#endif
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to create directories in lock path, locking will not be feasible.\r\nPath: {Path}\r\nException: {Exception.Name}",
                    _path, ex.GetBaseException().GetType().Name);
                return null;
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
                _logger.LogError(ex, "Unable to create directories in lock path, locking will not be feasible.\r\nPath: {Path}\r\nException: {Exception.Name}",
                    _path, ex.GetBaseException().GetType().Name);
                return null;
            }

            var lockFullFileNamePath = GetLockFileName(_path, name);

            // We share ReadWrite so that we overlap with an open lock (unshared write) and other requests (open reads).
            var fileShare = FileShare.ReadWrite;

            if (_isWindows == false) // on some unix file systems FileShare isn't implemented fully so we have to use a different approach
            {
                lockFullFileNamePath += "req"; 
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
                _logger.LogError(ex, "Unable to create directories in lock path, locking will not be feasible.\r\nPath: {Path}\r\nException: {Exception.Name}",
                    _path, ex.GetBaseException().GetType().Name);
                return false;
            }

            var lockFullFileNamePath = GetLockFileName(_path, name);

            // We share Write because we'll check this while we already have an unshared write open!
            FileAccess fileAccess = FileAccess.Read;
            FileShare fileShare = FileShare.Write;
            bool deleteOnClose = false; // This overlaps with holding a write lock, so don't delete the file when successful.

            if (_isWindows == false) 
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
