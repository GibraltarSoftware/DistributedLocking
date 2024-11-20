#region File Header and License
// /*
//    FileLock.cs
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
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace Gibraltar.DistributedLocking.Internal
{
    /// <summary>
    /// A wrapper for conveniently holding a file lock where the stream access is not necessarily needed.
    /// </summary>
    internal sealed class FileLock : IDisposable
    {
        private readonly string _fileName;
        private readonly FileMode _creationMode;
        private readonly FileShare _fileShare;
        private readonly FileAccess _fileAccess;
        private readonly bool _deleteOnClose;
        private readonly bool _isWindows;

        private readonly SafeFileHandle _fileHandle;
        private FileStream _fileStream;
        private bool _haveStream;

        private FileLock(string fileName, FileMode creationMode, FileAccess fileAccess, FileShare fileShare, bool manualDeleteOnClose)
        {
            _fileName = fileName;
            _creationMode = creationMode;
            _fileShare = fileShare;
            _fileAccess = fileAccess;
            _deleteOnClose = manualDeleteOnClose;
            _isWindows = Environment.OSVersion.Platform == PlatformID.Win32NT;
        }

        internal FileLock(SafeFileHandle fileHandle, string fileName, FileMode creationMode, FileAccess fileAccess, FileShare fileShare, bool manualDeleteOnClose)
            : this(fileName, creationMode, fileAccess, fileShare, manualDeleteOnClose)
        {
            _fileHandle = fileHandle;
            _fileStream = null;
            _haveStream = false;
        }

        internal FileLock(FileStream fileStream, string fileName, FileMode creationMode, FileAccess fileAccess, FileShare fileShare, bool manualDeleteOnClose)
            : this(fileName, creationMode, fileAccess, fileShare, manualDeleteOnClose)
        {
            _fileHandle = null;
            _fileStream = fileStream;
            _haveStream = (_fileStream != null);
        }

        /// <summary>
        /// Get the FileStream for this lock instance.
        /// </summary>
        /// <returns></returns>
        public FileStream GetFileStream()
        {
            if (_haveStream == false && _fileHandle != null &&
                _fileHandle.IsInvalid == false && _fileHandle.IsClosed == false)
            {
                try
                {
                    _fileStream = new FileStream(_fileHandle, _fileAccess);
                    _haveStream = true;
                }
                catch (Exception ex)
                {
                    GC.KeepAlive(ex);
                }
            }

            return _fileStream;
        }

        /// <summary>
        /// Release the file lock and the resources held by this instance.
        /// </summary>
        public void Dispose()
        {
            if (_deleteOnClose && _isWindows == false)
            {
                // For unix, delete it while we still have it open (exclusively) to avoid a race condition.
                SafeDeleteFile(_fileName); // Opens don't stop deletes!
            }

            if (_haveStream)
                _fileStream.Dispose();
            else if ((_fileHandle != null) && (!_fileHandle.IsClosed) && (!_fileHandle.IsInvalid)) //this is a punt to solve a MONO crash.
                _fileHandle.Dispose();

            _haveStream = false;
            _fileStream = null;

            //and now we try to delete it if we were supposed to.
            if (_deleteOnClose && _isWindows)
            {
                // On Windows - we can only delete it after we close it
                SafeDeleteFile(_fileName); // Delete will fail if anyone else has it open.  That's okay.
            }
        }


        /// <summary>
        /// Delete a file with no exception being thrown. Uses DeleteFile method if not running under Mono.
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        private static bool SafeDeleteFile(string fileName)
        {
            bool fileDeleted = false;
            try
            {
                File.Delete(fileName);
                fileDeleted = true; //same difference...
            }
            catch (Exception)
            {
            }

            return fileDeleted;
        }
    }
}
