﻿// /*
//    SqlLockProvider.cs
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
using System.Data.SqlClient;
using System.Diagnostics;
using Gibraltar.DistributedLocking.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gibraltar.DistributedLocking
{
    /// <summary>
    /// Provide distributed locks via a SQL Server Database
    /// </summary>
    /// <remarks>Locks are done using SQL Server's lock manager and do not affect or require any schema.</remarks>
    [DebuggerDisplay("Database: {Name}")]
    public class SqlLockProvider : IDistributedLockProvider
    {
        private readonly string _connectionString;
        private readonly ILogger<SqlLockProvider> _logger;
        private readonly int _queryTimeout = 30;

        /// <summary>
        /// Create a new connection string to the database defining the scope of the lock
        /// </summary>
        /// <param name="connectionString">The full connection string for the SQL Server and database to use for locking</param>
        public SqlLockProvider(string connectionString)
            :this(connectionString, NullLoggerFactory.Instance.CreateLogger<SqlLockProvider>()) 
        {
        }

        /// <summary>
        /// Create a new connection string to the database defining the scope of the lock
        /// </summary>
        /// <param name="connectionString">The full connection string for the SQL Server and database to use for locking</param>
        /// <param name="logger">Logger to use for diagnostics</param>
        public SqlLockProvider(string connectionString, ILogger<SqlLockProvider> logger)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentNullException(nameof(connectionString));

            _connectionString = connectionString;
            _logger = logger;

            //parse it so we can create a nice name and force an option..
            var connStringBuilder = new SqlConnectionStringBuilder(_connectionString);
            Name = $"{connStringBuilder.DataSource}:{connStringBuilder.InitialCatalog}";
            connStringBuilder.ApplicationName = "Distributed Lock Provider"; //so we will get our own pool in the process
            connStringBuilder.MaxPoolSize = Math.Max(connStringBuilder.MaxPoolSize, 250); //we hold connections while a lock is held, so we chew up connections
            _connectionString = connStringBuilder.ToString();
        }

        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        public IDisposable GetLock(string name)
        {
            SqlLock sqlLock = null;
            try
            {
                sqlLock = new SqlLock(_connectionString);

                var result = sqlLock.GetApplicationLock(name, SqlLockMode.Update, 0);
                if (result >= 0)
                    return sqlLock;

                sqlLock.SafeDispose();
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Unable to get lock due to {Exception.Name}\r\n{Exception.Message}", ex.GetBaseException().GetType(), ex.GetBaseException().Message);
                sqlLock.SafeDispose();
            }

            return null;
        }

        /// <inheritdoc />
        public IDisposable GetLockRequest(string name)
        {
            SqlLock sqlLock = null;
            try
            {
                sqlLock = new SqlLock(_connectionString);

                var requestLockName = GetRequestLockName(name);

                var result = sqlLock.GetApplicationLock(requestLockName, SqlLockMode.Shared, 0);
                if (result >= 0)
                    return sqlLock;

                sqlLock.SafeDispose();
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Unable to get lock request due to {Exception.Name}\r\n{Exception.Message}", ex.GetBaseException().GetType(), ex.GetBaseException().Message);
                sqlLock.SafeDispose();
            }

            return null;
        }

        /// <inheritdoc />
        public bool CheckLockRequest(string name)
        {
            bool lockRequestPending = false;
            using (var sqlLock = new SqlLock(_connectionString))
            {
                try
                {
                    var requestLockName = GetRequestLockName(name);

                    //if there are no other threads trying to request a lock then we'll get an exclusive
                    //lock on it. Otherwise we won't :)
                    var result = sqlLock.PeekApplicationLock(requestLockName, SqlLockMode.Exclusive);
                    if (result < 1)
                    {
                        lockRequestPending = true;
                    }
                }
                catch (Exception ex)
                {
                    //we don't care why we failed, we presume that means there is no pending request.
                    _logger.LogInformation(ex, "Unable to check lock request due to {Exception.Name}\r\n{Exception.Message}", ex.GetBaseException().GetType(), ex.GetBaseException().Message);
                    sqlLock.SafeDispose();
                }
            }

            return lockRequestPending;
        }

        private string GetRequestLockName(string lockName)
        {
            //we create a name that is very much unlikely to collide with a user intended name...
            return lockName + "~RequestToLock";
        }
    }
}
