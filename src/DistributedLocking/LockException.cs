#region File Header and License
// /*
//    LockException.cs
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

namespace Gibraltar.DistributedLocking
{
    /// <summary>
    /// Represents exceptions within the Distributed Locking system.
    /// </summary>
    [Serializable]
    public class LockException : Exception
    {
        #region Debugging assistance
        /// <summary>
        /// A temporary flag to tell us whether to invoke a Debugger.Break() on all of our exceptions.
        /// </summary>
        /// <remarks>True enables breakpointing, false disables.  This should probably be replaced with an enum
        /// to support multiple modes, assuming the basic usage works out.</remarks>
        // Note: The ReSharper complaint-disable comments can be removed once this is referenced for configuration elsewhere.
        // ReSharper disable ConvertToConstant
        private static bool s_BreakPointGibraltarExceptions = false; // Can be changed in the debugger
        // ReSharper restore ConvertToConstant

        /// <summary>
        /// Automatically stop debugger like a breakpoint, if enabled.
        /// </summary>
        /// <remarks>This will check the state of GibraltarExceptions.s_BreakPointGibraltarExceptions</remarks>
        [Conditional("DEBUG")]
        // ReSharper disable MemberCanBeMadeStatic
        private void BreakPoint() // Deliberately not static so that "this" exists when the breakpoint hits, for convenience.
            // ReSharper restore MemberCanBeMadeStatic
        {
            if (s_BreakPointGibraltarExceptions && Debugger.IsAttached)
            {
                Debugger.Break(); // Stop here only when debugging
                // ...then Shift-F11 as needed to step out to where it is getting created...
                // ...hopefully to the point just before it gets thrown.
            }
        }
        #endregion

        /// <summary>
        /// Initializes a new instance of the GibraltarException class.
        /// </summary>
        /// <remarks>This constructor initializes the Message property of the new instance to a system-supplied
        /// message that describes the error and takes into account the current system culture.
        /// For more information, see the base constructor in Exception.</remarks>
        public LockException()
        {
            // Just the base default constructor, except...
            BreakPoint();
        }

        /// <summary>
        /// Initializes a new instance of the LockException class with a specified error message.
        /// </summary>
        /// <param name="message">The error message string.</param>
        /// <remarks>This constructor initializes the Message property of the new instance using the
        /// message parameter.  The InnerException property is left as a null reference.
        /// For more information, see the base constructor in Exception.</remarks>
        public LockException(string message)
            : base(message)
        {
            // Just the base constructor, except...
            BreakPoint();
        }

        /// <summary>
        /// Initializes a new instance of the LockException class with a specified error message
        /// and a reference to the inner exception that is the cause of this exception.
        /// </summary>
        /// <param name="message">The error message string.</param>
        /// <param name="innerException">The exception that is the cause of the current exception, or a
        /// null reference if no inner exception is specified.</param>
        /// <remarks>An exception that is thrown as a direct result of a previous exception should include
        /// a reference to the previous exception in the innerException parameter.
        /// For more information, see the base constructor in Exception.</remarks>
        public LockException(string message, Exception innerException)
            : base(message, innerException)
        {
            // Just the base constructor, except...
            BreakPoint();
        }

        /// <summary>
        /// Initializes a new instance of the LockException class with serialized data.
        /// </summary>
        /// <param name="info">The SerializationInfo that holds the serialized object data about
        /// the exception being thrown.</param>
        /// <param name="context">The StreamingContext that contains contextual information about
        /// the source or destination.</param>
        /// <remarks>This constructor is called during deserialization to reconstitute the exception object
        /// transmitted over a stream.  For more information, see the base constructor in Exception.</remarks>
        protected LockException(System.Runtime.Serialization.SerializationInfo info,
                                     System.Runtime.Serialization.StreamingContext context)
            : base(info, context)
        {
            // Just the base constructor, except...
            BreakPoint();
        }
    }
}