/* Copyright (C) 2009-2014, Manuel Meitinger
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
using System.Threading;
using Aufbauwerk.ServiceProcess;

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
        private readonly Guid[] hardwareClasses;
        private readonly Thread thread;

        /// <summary>
        /// Creates a new background worker.
        /// </summary>
        /// <param name="name">The user-friendly name.</param>
        /// <param name="retryInterval">The amount of milliseconds to wait upon an error or 0 to wait indefinitely.</param>
        /// <param name="hardwareClasses">The hardware classes <see cref="System.Guid"/>s upon which the task relies on.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="name"/> or <paramref name="hardwareClasses"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="retryInterval"/> is negative.</exception>
        public BackgroundTask(string name, int retryInterval, params Guid[] hardwareClasses)
        {
            // check the input and create the thread
            if (name == null)
                throw new ArgumentNullException("name");
            if (hardwareClasses == null)
                throw new ArgumentNullException("hardwareClasses");
            if (retryInterval < 0)
                throw new ArgumentOutOfRangeException("retryInterval");
            this.name = name;
            this.retryInterval = retryInterval;
            var hardwareClassesCopy = (Guid[])hardwareClasses.Clone();
            Array.Sort(hardwareClassesCopy);
            this.hardwareClasses = hardwareClassesCopy;
            this.thread = new Thread(Run)
            {
                IsBackground = true,
                Name = name,
            };
        }

        private void OnDeviceEvent(object sender, DeviceEventArgs e)
        {
            // initiate a retry if the proper hardware was plugged in
            if (e.Type == DeviceEventType.Arrival && e.DeviceType == DeviceEventDeviceType.Interface && Array.BinarySearch(hardwareClasses, ((InterfaceDeviceEventArgs)e).ClassGuid) > -1)
                EndWait();
        }

        /// <summary>
        /// Logs an error.
        /// </summary>
        /// <param name="e">The <see cref="System.Exception"/> that occurred.</param>
        protected void Log(Exception e)
        {
            ServiceApplication.LogEvent(EventLogEntryType.Error, Properties.Resources.Program_BackgroundTaskException, name, e.Message);
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
            // start the thread and hook the device event
            thread.Start();
            ServiceApplication.DeviceEvent += OnDeviceEvent;
        }

        /// <summary>
        /// Aborts the task.
        /// </summary>
        public void Stop()
        {
            // abort the thread and unhook the device event
            thread.Abort();
            ServiceApplication.DeviceEvent -= OnDeviceEvent;
        }
    }

    internal static class Program
    {
        private static void Main(string[] args)
        {
            // register all start/stop handlers and run the service
            ServiceApplication.Start += Hardware.Port.Start;
            ServiceApplication.Stop += Hardware.Port.Stop;
            if (Properties.Settings.Default.RemotingEnabled)
            {
                ServiceApplication.Start += Remoting.Service.Start;
                ServiceApplication.Stop += Remoting.Service.Stop;
            }
            if (Properties.Settings.Default.AsteriskEnabled)
            {
                ServiceApplication.Start += Manager.AjamServer.Start;
                ServiceApplication.Stop += Manager.AjamServer.Stop;
            }
            ServiceApplication.Run();
        }
    }
}
