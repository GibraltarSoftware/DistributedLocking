#region File Header and License
// /*
//    OtherThreadLockHelper.cs
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

namespace Gibraltar.DistributedLocking.Test
{

    /// <summary>
    /// This class will create another thread to attempt to get (and hold) a multiprocess lock, to help in testing.
    /// </summary>
    /// <remarks>It is intended to be used in a using statement just like the RepositoryLock system for easy replacement.</remarks>
    internal class OtherThreadLockHelper : IDisposable
    {
        private readonly object m_Requester;
        private readonly DistributedLockManager m_LockManager;
        private readonly string m_Name;
        private readonly CancellationToken m_Cancellation;
        private readonly object m_Lock = new object();

        private DistributedLock m_RepositoryLock;
        private bool m_Exiting;
        private bool m_Exited;

        private OtherThreadLockHelper(object requester, DistributedLockManager lockManager, string lockName, CancellationToken token)
        {
            m_RepositoryLock = null;
            m_Requester = requester;
            m_LockManager = lockManager;
            m_Name = lockName;
            m_Cancellation = token;
        }

        public static OtherThreadLockHelper TryLock(object requester, DistributedLockManager lockManager, string multiprocessLockName, CancellationToken token = default)
        {
            var helper = new OtherThreadLockHelper(requester, lockManager, multiprocessLockName, token);
            if (helper.GetMultiprocessLock())
                return helper;

            helper.Dispose();
            return null;
        }

        private bool GetMultiprocessLock()
        {
            var helperThread = new Thread(HelperThreadStart);
            helperThread.TrySetApartmentState(ApartmentState.MTA);
            helperThread.Name = "Lock test helper";
            helperThread.IsBackground = true;
            lock (m_Lock)
            {
                helperThread.Start();

                Monitor.PulseAll(m_Lock);
                while (m_RepositoryLock == null && m_Exited == false)
                {
                    Monitor.Wait(m_Lock);
                }

                if (m_RepositoryLock != null)
                    return true;
                else
                    return false;
            }
        }

        private void HelperThreadStart()
        {
            DistributedLockManager.LockBarrier();

            lock (m_Lock)
            {
                m_LockManager.TryLock(this, m_Name, m_Cancellation, out m_RepositoryLock);

                if (m_RepositoryLock != null)
                {
                    Monitor.PulseAll(m_Lock);

                    while (m_Exiting == false)
                    {
                        Monitor.Wait(m_Lock); // Thread waits until we're told to exit.
                    }

                    m_RepositoryLock.Dispose(); // We're exiting, so it's time to release the lock!
                    m_RepositoryLock = null;
                }
                // Otherwise, we couldn't get the lock.

                m_Exited = true; // Lock is released and thread is exiting.
                Monitor.PulseAll(m_Lock);
            }
        }

        public void Dispose()
        {
            if (m_Exited)
                return;

            lock (m_Lock)
            {
                m_Exiting = true; // Signal the other thread it needs to release the lock and exit.

                System.Threading.Monitor.PulseAll(m_Lock); // Pulse that we changed the status.
                while (m_Exited == false)
                {
                    System.Threading.Monitor.Wait(m_Lock);
                }

                // We're now released, so we can return from the Dispose() call.
            }
        }

    } 
}
