#region File Header and License
// /*
//    SqlLock.cs
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
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Threading;

namespace Gibraltar.DistributedLocking.Internal
{
    internal sealed class SqlLock : IDisposable
    {
        /// <summary>
        /// The number of seconds we wait before retrying an operation that deadlocked.
        /// </summary>
        public const int DeadlockRetryDelay = 5;
        public const int DefaultQueryTimeout = 120;

        private readonly string _connectionString;

        private SqlConnection _connection;
        private SqlTransaction _transaction;
        private int _queryTimeout = DefaultQueryTimeout;

        internal SqlLock(string connectionString)
        {
            _connectionString = connectionString;
        }

        /// <summary>
        /// Determine if the requested lock can be acquired
        /// </summary>
        /// <remarks>This does not hold any locks outside of the call, making it less expensive for probing locks.</remarks>
        /// <returns>0 if the lock can't be granted, 1 if it can be.</returns>
        public int PeekApplicationLock(string name, SqlLockMode lockMode)
        {
            int result;
            using (var command = CreateCommand("SELECT APPLOCK_TEST('public', @Resource, @LockMode, @LockOwner)", GetConnection(), CommandType.Text, true))
            {
                CreateParameter(command, "Resource", DbType.String, ParameterDirection.Input, 255, name);
                CreateParameter(command, "LockMode", DbType.String, ParameterDirection.Input, 32, lockMode.ToString());
                CreateParameter(command, "LockOwner", DbType.String, ParameterDirection.Input, 32, "Session");

                result = ExecuteScalar<int>(command, false);
            }

            return result;
        }

        /// <summary>
        /// Attempt to get the specified lock in the database, which will remain associated with this object
        /// </summary>
        /// <param name="name"></param>
        /// <param name="lockMode"></param>
        /// <param name="lockTimeoutMilliseconds"></param>
        /// <returns>0 if the lock was granted, 1 if it was granted only after waiting, negative numbers for various failures.</returns>
        public int GetApplicationLock(string name, SqlLockMode lockMode, int lockTimeoutMilliseconds)
        {
            //if necessary bump out our query timeout
            if (lockTimeoutMilliseconds > 0)
            {
                var lockSeconds = lockTimeoutMilliseconds / 1000;
                _queryTimeout = Math.Max(lockSeconds, _queryTimeout);
            }

            using (var command = CreateCommand("sys.sp_GetAppLock", GetConnection(), CommandType.StoredProcedure, true))
            {
                var transaction = _transaction;
                if (transaction == null)
                {
                    if (command.Connection.State != ConnectionState.Open)
                    {
                        command.Connection.Open();
                    }

                    transaction = command.Connection.BeginTransaction();
                }
                command.Transaction = transaction;

                CreateParameter(command, "Resource", DbType.String, ParameterDirection.Input, 255, name);
                CreateParameter(command, "LockMode", DbType.String, ParameterDirection.Input, 32, lockMode.ToString());
                CreateParameter(command, "LockOwner", DbType.String, ParameterDirection.Input, 32, "Transaction");
                CreateParameter(command, "LockTimeout", DbType.String, ParameterDirection.Input, 32, lockTimeoutMilliseconds.ToString());
                var returnValue = CreateParameter(command, "ReturnValue", DbType.Int32, ParameterDirection.ReturnValue);

                command.CommandTimeout = _queryTimeout;

                ExecuteNonQuery(command, false);

                if ((returnValue.Value == null) || (returnValue.Value == DBNull.Value))
                {
                    transaction.SafeDispose();
                    return -100;
                }
                else
                {
                    _transaction = transaction; //since this is the true scope of our lock.
                    return (int)returnValue.Value;
                }
            }
        }

        /// <summary>
        /// Release the file lock and the resources held by this instance.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_transaction != null)
                {
                    try
                    {
                        _transaction.Commit(); //commit is faster than rollback, so we try..
                    }
                    catch (Exception ex)
                    {
                        GC.KeepAlive(ex);
                    }
                    finally
                    {
                        _transaction.SafeDispose();
                        _transaction = null;
                    }
                }

                _connection.SafeDispose();
                _connection = null;
            }
        }

        /// <summary>
        /// Get a SQL connection associated with this lock
        /// </summary>
        private SqlConnection GetConnection()
        {
            lock(this)
            {
                if (_connection == null)
                {
                    _connection = new SqlConnection(_connectionString);
                }

                return _connection;
            }
        }

        private SqlCommand CreateCommand(string commandText, DbConnection connection, CommandType commandType = CommandType.StoredProcedure, bool preventTimeout = false)
        {
            return new SqlCommand(commandText, (SqlConnection)connection) { CommandType = commandType, CommandTimeout = preventTimeout ? 0 : _queryTimeout };
        }

        /// <summary>
        /// Create a new parameter from the provided info
        /// </summary>
        /// <param name="command"></param>
        /// <param name="name">The name of the parameter (must match the SQL parameter name)</param>
        /// <param name="dbType">The SQL data type for the parameter</param>
        /// <param name="direction">The direction of data travel</param>
        /// <param name="size">The maximum length of string parameters</param>
        /// <param name="value">The initial value to assign to the parameter.</param>
        /// <returns></returns>
        private SqlParameter CreateParameter(SqlCommand command, string name, DbType dbType, ParameterDirection direction, int? size = null, object value = null)
        {
            var newParameter = command.CreateParameter();
            newParameter.ParameterName = name;
            newParameter.DbType = dbType;
            newParameter.Direction = direction;
            newParameter.Value = value;

            if (size.HasValue)
                newParameter.Size = size.Value;

            command.Parameters.Add(newParameter);

            return newParameter;
        }

        /// <summary>
        /// Execute a database command that doesn't return a dataset (or any output parameters) on the provided command
        /// </summary>
        /// <param name="command"></param>
        /// <param name="inRetry"></param>
        private void ExecuteNonQuery(DbCommand command, bool inRetry)
        {
            try
            {
                if (command.Connection.State != ConnectionState.Open)
                    command.Connection.Open();

                if (command.Connection.State != ConnectionState.Open)
                    throw new ApplicationException("Connection is unexpectedly not open.");

                command.ExecuteNonQuery();
            }
            //catch any database exceptions (not the one we generated)
            catch (SqlException ex)
            {
                if (inRetry)
                {
                    //now it's fatal, but throw our Gibraltar exception.
                    throw new InvalidOperationException(string.Format("SQL Exception {0}\r\n{1}", ex.Number, ex.ToMessage(command.Connection)), ex);
                }

                var isDeadlock = RethrowTerminalSqlExceptions((SqlCommand)command, ex);

                //and try again if it wasn't terminal.
                ExecuteNonQuery(command, (isDeadlock == false));
            }
        }

        /// <summary>
        /// Execute a database command that returns a single value and no dataset.
        /// </summary>
        private T ExecuteScalar<T>(DbCommand command, bool inRetry)
        {
            T returnVal;

            try
            {
                if (command.Connection.State != ConnectionState.Open)
                    command.Connection.Open();

                if (command.Connection.State != ConnectionState.Open)
                    throw new ApplicationException("Connection is unexpectedly not open.");

                var rawResult = command.ExecuteScalar();

                //we need to be careful with nulls to translate reasonably.
                if ((rawResult == null) || (rawResult == DBNull.Value))
                {
                    returnVal = default(T);
                }
                else
                {
                    returnVal = (T)rawResult;
                }
            }
            //catch any database exceptions (not the one we generated)
            catch (SqlException ex)
            {
                if (inRetry)
                {
                    //now it's fatal, but throw our Gibraltar exception.
                    throw new InvalidOperationException(string.Format("SQL Exception {0}\r\n{1}", ex.Number, ex.ToMessage(command.Connection)), ex);
                }

                var isDeadlock = RethrowTerminalSqlExceptions((SqlCommand)command, ex);

                //and try again if it wasn't terminal.
                returnVal = ExecuteScalar<T>(command, (isDeadlock == false));
            }

            return returnVal;
        }
        /// <summary>
        /// Checks to see if the error is a retryable or recoverable SQL exception.  If it is not, it translates it to an InvalidOperationException exception and rethrows.
        /// </summary>
        private bool RethrowTerminalSqlExceptions(SqlCommand command, SqlException ex)
        {
            bool isConnectionError = false;
            bool isDeadlockError = false;
            bool isTimeoutError = false;

            foreach (SqlError error in ex.Errors)
            {
                switch (error.Number)
                {
                    case 20: //The instance of SQL Server you attempted to connect to does not support encryption. (PMcE: amazingly, this is transient)
                    case 64: //A connection was successfully established with the server, but then an error occurred during the login process.
                    case 233: //The client was unable to establish a connection because of an error during connection initialization process before login
                    case 10053: //transport level error
                    case 10054: //transport level error
                    case 10060: //network or instance error
                    case 10061: //network or instance error
                    case 10928: //Azure out of resources
                    case 10929: //Azure out of resources
                    case 40143: //connection could not be initialized
                    case 40197: //the service encountered an error
                    case 40501: //service is busy
                    case 40613: //database unavailable
                        isConnectionError = true;
                        break;
                    case 3960: //snapshot isolation error
                    case 1205: //deadlock
                        isDeadlockError = true;
                        break;
                    case -2: //timeout
                        isTimeoutError = true;
                        break;
                    case 50000: //explicit TSQL RAISERROR
                        //see if this is a rethrow if a deadlock..
                        if (error.Message.Contains("Error 1205"))
                        {
                            isDeadlockError = true;
                        }
                        break;
                }
            }

            //the order we check these sets our priority of handling them.
            if (isDeadlockError)
            {
#if DEBUG
                Trace.TraceWarning("Retryable Sql Server Exception detected (deadlock))\r\nWhile performing a database call a deadlock error was detected.  We'll retry the operation.\r\nCommand: {0}\r\nConnection: {1}\r\nException: {2}", command.CommandText, command.Connection.ToMessage(), ex.Message);
#endif
                Thread.Sleep(DeadlockRetryDelay * 1000);
            }
            else if (isConnectionError)
            {
#if DEBUG
                Trace.TraceWarning("Retryable Sql Server Exception detected (connectivity error)\r\nWhile performing a database call a transient connection error was detected.  We'll retry the operation.\r\nCommand: {0}\r\nConnection: {1}\r\nException: {2}", command.CommandText, command.Connection.ToMessage(), ex.Message);
#endif
            }
            else if (isTimeoutError)
            {
#if DEBUG
                Trace.TraceWarning("Retryable Sql Server Exception detected (timeout)\r\nWhile performing a database call a timeout error was detected.  We'll retry the operation.\r\nCommand: {0}\r\nTimeout: {1:N0}\r\nConnection: {2}\r\nException: {3}", command.CommandText, command.CommandTimeout, command.Connection.ToMessage(), ex.Message);
#endif
            }
            else
            {
                //any other scenario treat it as fatal.
                throw new InvalidOperationException("Unable to acquire lock from database", ex);
            }

            return isDeadlockError;
        }
    }
}
