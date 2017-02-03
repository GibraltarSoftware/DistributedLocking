
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
            if (_deleteOnClose && CommonCentralLogic.IsMonoRuntime)
            {
                // For Mono, delete it while we still have it open (exclusively) to avoid a race condition.
                Win32Helper.SafeDeleteFile(_fileName); // Opens don't stop deletes!
            }

            if (_haveStream)
                _fileStream.Dispose();
            else if ((_fileHandle != null) && (!_fileHandle.IsClosed) && (!_fileHandle.IsInvalid)) //this is a punt to solve a MONO crash.
                _fileHandle.Dispose();

            _haveStream = false;
            _fileStream = null;

            //and now we try to delete it if we were supposed to.
            if (_deleteOnClose && CommonCentralLogic.IsMonoRuntime == false)
            {
                // Not Mono, we can only delete it after we have closed it.
                Win32Helper.SafeDeleteFile(_fileName); // Delete will fail if anyone else has it open.  That's okay.
            }
        }
    }
}
