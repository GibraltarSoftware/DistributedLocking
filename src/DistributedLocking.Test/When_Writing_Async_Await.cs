using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Gibraltar.DistributedLocking.Test
{
    [TestFixture]
    public class When_Writing_Async_Await
    {
        [Test]
        public async Task Get_Distinct_Lock_Ids_For_Peer_Threads()
        {
            var previousIds = new HashSet<Guid>();

            //lock in our context (so we have a consistent result)
            var ourId = ReadLockId();
            previousIds.Add(ourId);

            //now grab it off our helper..
            var childId = Task.Factory.StartNew(ResetAndReadLockId, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).Result;
            Assert.That(previousIds.Contains(childId), Is.False);
            previousIds.Add(childId);

            childId = Task.Factory.StartNew(ResetAndReadLockId).Result;
            Assert.That(previousIds.Contains(childId), Is.False);
            previousIds.Add(childId);

            childId = await Task.Run(ResetAndReadLockIdAsync);
            Assert.That(previousIds.Contains(childId), Is.False);
            previousIds.Add(childId);

            childId = Task.Run(() => DistributedLockManager.CurrentLockId).Result;
            Assert.That(ourId.Equals(childId), Is.True); 
        }

        [Test]
        public async Task Get_Same_Lock_Id_For_Parent_After_Child_Barrier()
        {
            //lock in our context (so we have a consistent result)
            var ourId = ReadLockId();

            //Go to our children which should have their own Id
            var childId = await ResetAndReadLockIdAsync();

            Assert.That(ourId, Is.Not.EqualTo(childId));

            //but we better still get our id
            Assert.That(ReadLockId(), Is.EqualTo(ourId));
        }

        private async Task<Guid> ResetAndReadLockIdAsync()
        {
            DistributedLockManager.LockBarrier();

            await Task.Delay(0); //just to force us to yield.

            return await ReadLockIdAsync();
        }

        private async Task<Guid> ReadLockIdAsync()
        {
            await Task.Delay(0); //just to force us to yield.

            return ReadLockId();
        }

        private Guid ResetAndReadLockId()
        {
            DistributedLockManager.LockBarrier();

            return ReadLockId();
        }

        private Guid ReadLockId()
        {
            return DistributedLockManager.CurrentLockId;
        }
    }
}
