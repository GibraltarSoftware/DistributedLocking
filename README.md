# Gibraltar.DistributedLocking

DistributedLocking provides an easy to use, battle-tested method of doing distributed (between processes and computers) locks
in a .NET application.  This library is used within the Loupe Logging Cloud SaaS to protect operations that need to lock
more than a single resource.  For example, it's used during tenant database upgrades and maintenance to ensure two different
processes don't attempt to perform schema changes at the same time.  

The easiest way to add this library to your project is via the official NuGet package, [Gibraltar.DistributedLocking](https://www.nuget.org/packages/Gibraltar.DistributedLocking).

## Easy to Use with Using

Using DistributedLocking is easy:  You define a DistributedLockManager which determines the scope of a set of locks 
and the underlying infrastructure used to perform distributed locks (files or SQL Server) and then ask it for named locks.
You can provide a timeout for how long you'll wait for the lock or specify zero to give up immediately if it isn't available.
```CS
var lockManager = new DistributedLockManager(new SqlLockProvider(_connectionString));

//We want to get a lock and will wait up to 60 seconds for it.
using (lockManager.Lock(this, "My Lock Name", 60))
{
	//if we get here, we've got the lock.  Otherwise, a LockTimeoutException would be thrown.
}

```

You can also try to get locks but take different action if they aren't available
```C#
var lockManager = new DistributedLockManager(new SqlLockProvider(_connectionString));

DistributedLock timeoutLock;
If (lockManager.TryLock(this, MultiprocessLockName, 5, out timeoutLock) == false)
{
	//No lock, lets go off and do something else and try again later...
}
else
{
	using (timeoutLock)	
	{
		//We've got the lock, we're KING OF THE WORLD!
	}				
}
```

You will typically want to have a central DistributedLockManager for your application (or for a scope 
within it, such as a single tenant or customer).  You can provision this once in a common place and then
inject it wherever you need to access distributed locks.  Creating multiple DistributedLockManager instances
for the same scope can prevent some high performance optimizations but will not result in incorrect operation.

## Re-entrant Locks

A common challenge with locking is to ensure that the lock is acquired everywhere it needs to be without having
to determine if it has already been acquired (and then pass the lock around).  DistributedLocking is designed
to detect that the current thread of execute already has the lock that is being requested and add a new child
scope to that lock very quickly and efficiently.  The lock is only released when all requesters have released
their lock.
```CS
var lockManager = new DistributedLockManager(new SqlLockProvider(_connectionString));

DistributedLock innermostLock = null;

//We want to get a lock and will wait up to 60 seconds for it.
using (lockManager.Lock(this, "My Lock Name", 60))
{
	//Now lets get the lock again, simulating a situation where a child method
	//is requesting the lock without knowing its caller already has it.
	using (lockManager.Lock(this, "My Lock Name", 60))
	{
		//Even more creatively, we can get the lock again and keep it, outside of the 
		//scope of execution of our parent locking methods.
		innermostLock = lockManager.Lock(this, "My Lock Name", 60);
	}
}
//even though the two using statements have completed, our lock is not released yet..

innermostLock.Dispose(); //until now.  Right now it gets released.
```

## Just Avoid Async/Await

Due to the way the internals of the DistributedLockManager work it isn't presently safe to use with
async/await within a lock.  The reason is that it makes assumptions about locks being associatd with
threads that are not valid once you start using async/await within an area that is locked with this 
library.

# How It Works

## Fast In-process Locks

DistributedLocking uses a two-tier strategy where it uses in-memory queues and lock objects to handle
lock competition within the same process and then uses an external locking provider to handle isolation
between different instances of DistributedLockManager.  This approach is optimized to minimize overhead
when compared to a simple lock(object) statement when only one process is using a particular lock and 
then back off and allow locks to move between processes when there are multiple processes contending for
the same lock.

## Slower External Locks

To ensure only a single instance of DistributedLockManager has a lock at any one time an external 
lock provider is used.  Two providers are built in:
* **FileLockProvider**: Uses a directory on a local disk or network share for scope and individual files
to represent specific locks and requests.  Works with Windows and Unix.
* **SqlLockProvider**:  Uses the sp_GetAppLock feature of SQL Server 2005 and later to acquire and test locks.

If you want you can create your own implementation, as long as it implements the IDistributedLockProvider 
interface.  Most of the complexity has been abstracted away from these providers however it is recommended
that you ensure your provider passes the same battery of unit tests the other providers do.

## How Fast is It?

You can see for yourself by running the Can_Acquire_Lock_Many_Times() unit test.  This test acquires and
releases the lock 1000 times in a tight loop with a zero timeout.  For a local file system this typically takes
less than a second and for a SQL Server on a local network it takes around 4 seconds.  

This demonstrates several points:
* Locks are being released immediately when the object is disposed (since they can be immediately reacquired)
* This is a worst case performance where the locks aren't being shared within the same process.  If there is 
lock contention within a single process it's much faster to switch which party has the lock as it doesn't require
releasing and reacquiring the external lock.

## How Long can a Lock Be Held?

With the FileLockProvider there is no limit - a lock can be held as long as the .NET application domain is alive and
there is a reference to the lock object.  

With the SqlLockProvider it is limited to the longest time a transaction can be outstanding on your SQL Server or 
the default maxTimeout for system.Transaction in your .NET application.  By default this is 10 minutes.  Once a lock
times out the next attempt to reacquire it within DistributedLocking will re-establish it if another process hasn't 
acquired it.  If another process has acquired it, the local lock will be released with a LockTimeoutException.

## Using with SQL Server / SQL Azure

Since SQL 2005, SQL Server (and SQL Azure) has exposed its internal lock manager to applications via 
sp_GetAppLock and a few related functions.  These have good resiliency approaches with locks automatically
being released if a connection is reset or transaction rolled back.  It can also detect deadlocks in scenarios
where multiple named locks are being used.

No specific schema is required to use this feature, just a user database and a connection string that is a 
member of public and can call sp_GetAppLock.  The names used for locks can be arbitrary and don't need to
reflect any element of the SQL Database schema (but it doesn't matter if they do, they will not lock the
SQL object with the same name).


# How To Build This Project

This project is designed for use with Visual Studio 2012 with NuGet package restore enabled. When you build it the first time 
it will retrieve dependencies from NuGet.  Building in release mode will automatically generate a new NuGet package.

# Contributing

Feel free to branch this project and contribute a pull request to the development branch. If your changes are incorporated into 
the master version they'll be published out to NuGet for everyone to use!