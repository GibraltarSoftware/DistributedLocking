namespace Gibraltar.DistributedLocking.Internal
{
    /// <summary>
    /// The lock level being requested
    /// </summary>
    internal enum SqlLockMode
    {
        /// <summary>
        /// Multiple readers allowed, no updates or exclusive
        /// </summary>
        Shared,

        /// <summary>
        /// Single writer, multiple readers allowed.
        /// </summary>
        Update,

        /// <summary>
        /// Exclusive, no other readers or writers allowed
        /// </summary>
        Exclusive
    }
}
