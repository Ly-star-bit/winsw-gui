using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using static WinSW.Native.JobApis;

namespace WinSW.Native
{
    internal static class JobObject
    {
        /// <summary>
        /// Creates an unnamed job that ends every process in it once the last handle to it is
        /// closed, and that lets a process in it start another outside it by asking to
        /// (<see cref="ProcessApis.CREATE_BREAKAWAY_FROM_JOB"/>). The handle is not inheritable,
        /// so a child that is started with inherited handles does not keep the job alive.
        /// </summary>
        /// <exception cref="Win32Exception" />
        internal static Handle CreateKillOnClose()
        {
            var job = CreateJobObjectW();
            if (job == IntPtr.Zero)
            {
                throw new Win32Exception();
            }

            var information = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK,
                },
            };

            if (!SetInformationJobObject(
                job,
                JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                information,
                Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                var exception = new Win32Exception();

                // Nothing is in the job yet, so closing it ends nothing.
                _ = HandleApis.CloseHandle(job);
                throw exception;
            }

            return new Handle(job);
        }

        /// <exception cref="Win32Exception" />
        internal static void Assign(IntPtr job, IntPtr process)
        {
            if (!AssignProcessToJobObject(job, process))
            {
                throw new Win32Exception();
            }
        }
    }
}
