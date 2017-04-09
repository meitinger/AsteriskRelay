/* Copyright (C) 2009-2017, Manuel Meitinger
 * 
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 2 of the License, or
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

namespace Aufbauwerk.Asterisk.Relay
{
    /// <summary>
    /// Base class for all background tasks.
    /// </summary>
    public abstract class BackgroundTask
    {
        private readonly object retryObject = new object();
        private readonly string name;
        private readonly int retryInterval;
        private readonly Thread thread;

        /// <summary>
        /// Creates a new background worker.
        /// </summary>
        /// <param name="name">The user-friendly name.</param>
        /// <param name="retryInterval">The amount of milliseconds to wait upon an error or 0 to wait indefinitely.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="name"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="retryInterval"/> is negative.</exception>
        public BackgroundTask(string name, int retryInterval)
        {
            // check the input and create the thread
            if (name == null)
                throw new ArgumentNullException("name");
            if (retryInterval < 0)
                throw new ArgumentOutOfRangeException("retryInterval");
            this.name = name;
            this.retryInterval = retryInterval;
            this.thread = new Thread(Run)
            {
                IsBackground = true,
                Name = name,
            };
        }

        /// <summary>
        /// Logs an error.
        /// </summary>
        /// <param name="e">The <see cref="System.Exception"/> that occurred.</param>
        protected void Log(Exception e)
        {
            Program.LogEvent(EventLogEntryType.Error, Properties.Resources.Program_BackgroundTaskException, name, e.Message);
        }

        /// <summary>
        /// Waits until one of the following events occurs:
        /// <list type="bullet">
        /// <item>The configured time-out is reached.</item>
        /// <item><see cref="EndWait"/> is called.</item>
        /// <item>A device that affects this task arrives.</item>
        /// </list>
        /// </summary>
        protected void Wait()
        {
            lock (retryObject)
            {
                if (retryInterval == 0)
                    Monitor.Wait(retryObject);
                else
                    Monitor.Wait(retryObject, retryInterval);
            }
        }

        /// <summary>
        /// Ends the <see cref="Wait"/> method if it's currently called.
        /// </summary>
        protected void EndWait()
        {
            lock (retryObject)
                Monitor.Pulse(retryObject);
        }

        /// <summary>
        /// Performs the actual task.
        /// </summary>
        protected abstract void Run();

        /// <summary>
        /// Starts the worker.
        /// </summary>
        /// <exception cref="System.Threading.ThreadStateException">The task has already been started.</exception>
        public void Start()
        {
            thread.Start();
        }

        /// <summary>
        /// Aborts the task.
        /// </summary>
        public void Stop()
        {
            thread.Abort();
        }
    }

    internal static class Program
    {
        private static readonly Service service = new Service();

        private class Service : ServiceBase
        {
            internal Service()
            {
                // specify the name and inform SCM that the service can be stopped
                ServiceName = "AsteriskRelay";
                CanStop = true;
            }

            protected override void OnStart(string[] args)
            {
                // set a non-zero exit code to indicate failure if something goes wrong and start
                ExitCode = ~0;
                Program.Start();
            }

            protected override void OnStop()
            {
                // stop and reset the exit code to indicate success
                Program.Stop();
                ExitCode = 0;
            }
        }

        private static void Start()
        {
            // start all threads
            Hardware.Port.StartAll();
            if (Properties.Settings.Default.RemotingEnabled)
                Remoting.Service.Start();
            if (Properties.Settings.Default.AsteriskEnabled)
                Manager.AjamServer.StartAll();
        }

        private static void Stop()
        {
            // stop all threads
            Hardware.Port.StopAll();
            if (Properties.Settings.Default.RemotingEnabled)
                Remoting.Service.Stop();
            if (Properties.Settings.Default.AsteriskEnabled)
                Manager.AjamServer.StopAll();
        }

        private static void UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            // properly log the exception and terminate
            if (e.ExceptionObject is Exception)
            {
                try { LogEvent(EventLogEntryType.Error, e.ExceptionObject.ToString()); }
                catch { return; }
                if (e.IsTerminating)
                    Environment.Exit(Marshal.GetHRForException((Exception)e.ExceptionObject));
            }
        }

        private static void Main(string[] args)
        {
            // handle all uncaught exceptions
            AppDomain.CurrentDomain.UnhandledException += new UnhandledExceptionEventHandler(UnhandledException);

#if DEBUG
            // repeat start/stop
            do
            {
                lock (service)
                    Console.WriteLine("Starting...");
                Start();
                lock (service)
                    Console.WriteLine("Started. Press ENTER to stop.");
                if (Console.ReadLine() == null)
                    break;
                lock (service)
                    Console.WriteLine("Stopping...");
                Stop();
                lock (service)
                    Console.WriteLine("Stopped. Press ENTER to start.");
            }
            while (Console.ReadLine() != null);
#else
            // run the service
            ServiceBase.Run(service);
#endif
        }

        internal static void RequestAdditionalTime(TimeSpan timeSpan)
        {
#if DEBUG
            // notify the user
            lock (service)
            {
                var previousColor = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.White;
                try { Console.WriteLine("Request additional {0}.", timeSpan); }
                finally { Console.ForegroundColor = previousColor; }
            }
#else
            // request additional time from the SCM
            service.RequestAdditionalTime((int)timeSpan.TotalMilliseconds);
#endif
        }

        internal static void LogEvent(EventLogEntryType type, string message)
        {
#if DEBUG
            // determine the color and write to console
            ConsoleColor color;
            switch (type)
            {
                case EventLogEntryType.FailureAudit:
                    color = ConsoleColor.Red;
                    goto case EventLogEntryType.SuccessAudit;
                case EventLogEntryType.SuccessAudit:
                    message = "[AUDIT] " + message;
                    goto case EventLogEntryType.Information;
                case EventLogEntryType.Error:
                    color = ConsoleColor.Red;
                    break;
                case EventLogEntryType.Warning:
                    color = ConsoleColor.Yellow;
                    break;
                case EventLogEntryType.Information:
                    color = ConsoleColor.Green;
                    break;
                default:
                    throw new System.ComponentModel.InvalidEnumArgumentException("type", (int)type, typeof(EventLogEntryType));
            }
            lock (service)
            {
                var previousColor = Console.ForegroundColor;
                Console.ForegroundColor = color;
                try { Console.WriteLine(message); }
                finally { Console.ForegroundColor = previousColor; }
            }
#else
            // write the log entry
            service.EventLog.WriteEntry(message, type);
#endif
        }

        internal static void LogEvent(EventLogEntryType type, string format, params object[] args)
        {
            LogEvent(type, string.Format(format, args));
        }
    }
}
