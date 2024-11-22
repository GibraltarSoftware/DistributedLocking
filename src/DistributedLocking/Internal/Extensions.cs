// /*
//    Extensions.cs
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
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Text;

namespace Gibraltar.DistributedLocking.Internal
{
    internal static class Extensions
    {
        /// <summary>
        /// Generate a more useful message for the provided SQL Exception
        /// </summary>
        /// <returns></returns>
        public static string ToMessage(this SqlException sqlException, DbConnection connection = null)
        {

            try
            {
                var messageBuilder = new StringBuilder(1024);

                messageBuilder.AppendLine(sqlException.Message);

                int errorIndex = 0;
                foreach (var error in sqlException.Errors)
                {
                    messageBuilder.AppendFormat("Error {0:N0}: {1}\r\n", errorIndex++, error);
                }

                if (connection != null)
                {
                    messageBuilder.AppendLine(connection.ToMessage());
                }

                return messageBuilder.ToString();
            }
            catch (Exception ex)
            {
                GC.KeepAlive(ex);

                return sqlException.Message;
            }
        }

        /// <summary>
        /// Create a text message describing the database connection
        /// </summary>
        /// <param name="connection"></param>
        /// <returns></returns>
        public static string ToMessage(this DbConnection connection)
        {
            if (connection == null)
                return "(None)";

            var stringBuilder = new StringBuilder(1024);

            stringBuilder.AppendFormat("Server:\r\n    DataSource: {0}\r\n    Database: {1}\r\n    Connection Timeout: {2:N0} Seconds\r\n    Provider: {3}\r\n",
                                       connection.DataSource, connection.Database, connection.ConnectionTimeout, connection.GetType());

            if (connection.State == ConnectionState.Open)
            {
                stringBuilder.AppendFormat("    Server Version: {0}\r\n", connection.ServerVersion);
            }

            return stringBuilder.ToString();
        }

        /// <summary>
        /// Dispose a nullable object if it is not null.
        /// </summary>
        /// <param name="disposable"></param>
        public static void SafeDispose(this IDisposable disposable)
        {
            if (ReferenceEquals(disposable, null))
                return;

            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                GC.KeepAlive(ex);
            }
        }
    }
}
