#region File Header and License
// /*
//    Win32Helper.cs
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
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Gibraltar.DistributedLocking.Internal
{
    /// <summary>
    /// A class to provide common wrappers and direct access to low-level Win32 pinvoke calls.
    /// </summary>
    internal static class Win32Helper
    {
        /// <summary>
        /// The CreateFile function creates or opens a file, file stream, directory, physical disk, volume, console buffer, tape drive,
        /// communications resource, mailslot, or named pipe. The function returns a handle that can be used to access an object.
        /// </summary>
        /// <param name="fileName">The full-path file name to create or open.</param>
        /// <param name="fileAccess">Desired access to the object, which can be read, write, or both</param>
        /// <param name="fileShare">The sharing mode of an object, which can be read, write, both, or none</param>
        /// <param name="securityAttributes">A pointer to a SECURITY_ATTRIBUTES structure that determines whether or not the returned handle can 
        /// be inherited by child processes. Can be null</param>
        /// <param name="creationDisposition">An action to take on files that exist and do not exist</param>
        /// <param name="flags">The file attributes and flags. </param>
        /// <param name="template">A handle to a template file with the GENERIC_READ access right. The template file supplies file attributes 
        /// and extended attributes for the file that is being created. This parameter can be null</param>
        /// <returns>If the function succeeds, the return value is an open handle to a specified file. If a specified file exists before the function 
        /// all and creationDisposition is CREATE_ALWAYS or OPEN_ALWAYS, a call to GetLastError returns ERROR_ALREADY_EXISTS, even when the function 
        /// succeeds. If a file does not exist before the call, GetLastError returns 0 (zero).
        /// If the function fails, the return value is INVALID_HANDLE_VALUE. To get extended error information, call GetLastError.
        /// </returns>
        [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        internal static extern SafeFileHandle CreateFile(string fileName,
                                                         [MarshalAs(UnmanagedType.U4)] FileAccess fileAccess,
                                                         [MarshalAs(UnmanagedType.U4)] FileShare fileShare,
                                                         IntPtr securityAttributes,
                                                         [MarshalAs(UnmanagedType.U4)] FileMode creationDisposition,
                                                         uint flags,
                                                         IntPtr template);

        [DllImport("kernel32.dll", SetLastError = false, CharSet = CharSet.Auto)]
        private static extern bool DeleteFile(string path);

        /// <summary>
        /// Attempt to open a FileStream while avoiding exceptions.
        /// </summary>
        /// <param name="fileName">The full-path file name to create or open.</param>
        /// <param name="creationMode">An action to take on files that exist and do not exist</param>
        /// <param name="fileAccess">Desired access to the object, which can be read, write, or both</param>
        /// <param name="fileShare">The sharing mode of an object, which can be read, write, both, or none</param>
        /// <returns>An open FileStream, or null upon failure.</returns>
        public static FileStream OpenFileStream(string fileName, FileMode creationMode, FileAccess fileAccess, FileShare fileShare)
        {
            FileStream fileStream = null;
            if (CommonCentralLogic.IsMonoRuntime)
            {
                // It's Mono, so we can't P/Invoke (except maybe on Windows).  So Just use the .NET functionality and try/catch.
                try
                {
                    fileStream = new FileStream(fileName, creationMode, fileAccess, fileShare); // What we're wrapping anyway.
                }
                catch (Exception ex)
                {
                    GC.KeepAlive(ex);
                    fileStream = null;
#if DEBUG
                    // ToDo: Filter this for the expected Exception types and don't log them.
                    Trace.TraceError("Error opening a FileStream: {0}\r\n{1}", ex.GetType().FullName, ex.Message, ex);
#endif
                }
            }
            else
            {
                // Not Mono, so we can use P/Invoke to avoid having to catch an exception for a rejected open.
                SafeFileHandle fileHandle = CreateFile(fileName, fileAccess, fileShare, IntPtr.Zero, creationMode, 0, IntPtr.Zero);

                if (fileHandle != null && fileHandle.IsInvalid == false && fileHandle.IsClosed == false)
                {
                    try // This should be safe from exceptions, but just in case...
                    {
                        fileStream = new FileStream(fileHandle, fileAccess);
                    }
                    catch (Exception ex)
                    {
                        GC.KeepAlive(ex);
#if DEBUG
                        Trace.TraceError("Error opening a FileStream for a handle: {0}\r\n{1}", ex.GetType().FullName, ex.Message, ex);
#endif
                        fileHandle.Dispose(); // Make sure the handle gets released.
                    }
                }
            }

            return fileStream;
        }

        /// <summary>
        /// Delete a file with no exception being thrown. Uses DeleteFile method if not running under Mono.
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        public static bool SafeDeleteFile(string fileName)
        {
            //There is really VERY little difference between the native and File.Delete except the latter will throw
            //exceptions for some types of Win32 errors, and we don't want them for any.
            if (CommonCentralLogic.IsMonoRuntime)
            {
                // It's Mono, so we can't P/Invoke (except maybe on Windows).  So Just use the .NET functionality and try/catch.
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
            else
            {
                // Not Mono, so we can use P/Invoke to avoid having to catch an exception for a rejected delete.
                return DeleteFile(fileName);
            }
        }


        /// <summary>
        /// Get a persistent lock on a file without opening it.
        /// </summary>
        /// <param name="fileName">The full-path file name to create or open.</param>
        /// <param name="creationMode">An action to take on files that exist and do not exist</param>
        /// <param name="fileAccess">Desired access to the object, which can be read, write, or both</param>
        /// <param name="fileShare">The sharing mode of an object, which can be read, write, both, or none</param>
        /// <returns></returns>
        public static FileLock GetFileLock(string fileName, FileMode creationMode, FileAccess fileAccess, FileShare fileShare)
        {
            FileLock fileLock = null;
            if (CommonCentralLogic.IsMonoRuntime)
            {
                FileStream fileStream;
                try
                {
                    fileStream = new FileStream(fileName, creationMode, fileAccess, fileShare);
                }
                catch
                {
                    fileStream = null;
                }

                if (fileStream != null)
                    fileLock = new FileLock(fileStream, fileName, creationMode, fileAccess, fileShare, false); //if they wanted delete, they had to use the explict option...
            }
            else
            {
                SafeFileHandle fileHandle = CreateFile(fileName, fileAccess, fileShare, IntPtr.Zero, creationMode, 0, IntPtr.Zero);

                if (fileHandle != null && (fileHandle.IsInvalid || fileHandle.IsClosed))
                {
                    //don't need the file handle, couldn't lock it.
                    fileHandle.Dispose(); // just so we're absolutely clear.

                    fileHandle = null;
                }

                if (fileHandle != null)
                    fileLock = new FileLock(fileHandle, fileName, creationMode, fileAccess, fileShare, false); //if they wanted delete, they had to use the explict option...
            }

            return fileLock;

        }
    }
}
