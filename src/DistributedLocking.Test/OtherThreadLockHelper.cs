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
        private readonly int m_Timeout;
        private readonly object m_Lock = new object();

        private DistributedLock m_RepositoryLock;
        private bool m_Exiting;
        private bool m_Exited;

        private OtherThreadLockHelper(object requester, DistributedLockManager lockManager, string lockName, int timeout)
        {
            m_RepositoryLock = null;
            m_Requester = requester;
            m_LockManager = lockManager;
            m_Name = lockName;
            m_Timeout = timeout;
        }

        public static OtherThreadLockHelper TryLock(object requester, DistributedLockManager lockManager, string multiprocessLockName, int timeout)
        {
            var helper = new OtherThreadLockHelper(requester, lockManager, multiprocessLockName, timeout);
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

                System.Threading.Monitor.PulseAll(m_Lock);
                while (m_RepositoryLock == null && m_Exited == false)
                {
                    System.Threading.Monitor.Wait(m_Lock);
                }

                if (m_RepositoryLock != null)
                    return true;
                else
                    return false;
            }
        }

        private void HelperThreadStart()
        {
            lock (m_Lock)
            {
                m_LockManager.TryLock(this, m_Name, m_Timeout, out m_RepositoryLock);

                if (m_RepositoryLock != null)
                {
                    System.Threading.Monitor.PulseAll(m_Lock);

                    while (m_Exiting == false)
                    {
                        System.Threading.Monitor.Wait(m_Lock); // Thread waits until we're told to exit.
                    }

                    m_RepositoryLock.Dispose(); // We're exiting, so it's time to release the lock!
                    m_RepositoryLock = null;
                }
                // Otherwise, we couldn't get the lock.

                m_Exited = true; // Lock is released and thread is exiting.
                System.Threading.Monitor.PulseAll(m_Lock);
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
