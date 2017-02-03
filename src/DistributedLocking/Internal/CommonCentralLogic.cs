#region File Header
// /********************************************************************
//  * COPYRIGHT:
//  *    This software program is furnished to the user under license
//  *    by Gibraltar Software Inc, and use thereof is subject to applicable 
//  *    U.S. and international law. This software program may not be 
//  *    reproduced, transmitted, or disclosed to third parties, in 
//  *    whole or in part, in any form or by any manner, electronic or
//  *    mechanical, without the express written consent of Gibraltar Software Inc,
//  *    except to the extent provided for by applicable license.
//  *
//  *    Copyright © 2008 - 2015 by Gibraltar Software, Inc.  
//  *    All rights reserved.
//  *******************************************************************/
#endregion
using System;
using System.Diagnostics;

namespace Gibraltar.DistributedLocking.Internal
{
    /// <summary>
    /// A static class to hold central logic for common file and OS operations needed by various projects.
    /// </summary>
    internal static class CommonCentralLogic
    {
        private static volatile bool s_BreakPointEnable = false; // Can be changed in the debugger

        /// <summary>
        /// Indicates if the process is running under the Mono runtime or the full .NET CLR.
        /// </summary>
        public static bool IsMonoRuntime { get; } = CheckForMono();

        /// <summary>
        /// Automatically stop debugger like a breakpoint, if enabled.
        /// </summary>
        /// <remarks>This will check the state of Log.BreakPointEnable and whether a debugger is attached,
        /// and will breakpoint only if both are true.  This should probably be extended to handle additional
        /// configuration options using an enum, assuming the basic usage works out.  This method is conditional
        /// upon a DEBUG build and will be safely ignored in release builds, so it is not necessary to wrap calls
        /// to this method in #if DEBUG (acts much like Debug class methods).</remarks>
        [Conditional("DEBUG")]
        public static void DebugBreak()
        {
            if (s_BreakPointEnable && Debugger.IsAttached)
            {
                Debugger.Break(); // Stop here only when debugging
                // ...then Shift-F11 to step out to where it is getting called...
            }
        }

        /// <summary>
        /// Indicates if the logging system should be running in silent mode (for example when running in the agent).
        /// </summary>
        public static bool SilentMode { get; set; } = false;

        /// <summary>
        /// Check whether we are running in a Mono runtime environment rather than a normal .NET CLR.
        /// </summary>
        /// <returns>True if running in Mono.  False if .NET CLR.</returns>
        private static bool CheckForMono()
        {
            Type monoRuntime = Type.GetType("Mono.Runtime"); // Detect if we're running under Mono runtime.
            bool isMonoRuntime = (monoRuntime != null); // We'll cache the result so we don't have to waste time checking again.

            return isMonoRuntime;
        }
    }
}
