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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using Aufbauwerk.Net.Asterisk;

namespace Aufbauwerk.Asterisk.Relay.Manager
{
    /// <summary>
    /// Defines the logic necessary to communicate with an Asterisk Manager.
    /// </summary>
    public class AjamServer : BackgroundTask
    {
        private const string SwitchOnState = "BUSY";
        private const string SwitchOffState = "NOT_INUSE";

        private static ICollection<AjamServer> instances;

        private static string ConvertState(Configuration.Switch s)
        {
            // return the Asterisk device state that corresponds to the switch state
            switch (s.State)
            {
                case Configuration.SwitchState.On: return SwitchOnState;
                case Configuration.SwitchState.Off: return SwitchOffState;
                default: return "UNKNOWN";
            }
        }

        internal static void StartAll()
        {
            // create and start all defined manager clients
            instances = new List<AjamServer>();
            foreach (var instanceConfiguration in Configuration.AsteriskManagerInterface.All)
            {
                var instance = new AjamServer(instanceConfiguration);
                instance.Start();
                instances.Add(instance);
            }
        }

        internal static void StopAll()
        {
            // stop the manager client
            if (instances != null)
                foreach (var instance in instances)
                    instance.Stop();
        }

        private readonly Queue<Configuration.Switch> updates = new Queue<Configuration.Switch>();
        private readonly AutoResetEvent newUpdates = new AutoResetEvent(false);
        private readonly Configuration.AsteriskManagerInterface configuration;

        private AjamServer(Configuration.AsteriskManagerInterface configuration)
            : base(string.Format(Properties.Resources.Manager_BackgroundTaskName, configuration.BaseUri), configuration.RetryInterval)
        {
            this.configuration = configuration;
        }

        private string BuildVarName(Configuration.Switch s)
        {
            return string.Format("DEVICE_STATE({0})", string.Format(configuration.DeviceNameFormat, s.Name));
        }

        private AsteriskAction SetVar(Configuration.Switch s)
        {
            return new AsteriskAction("SetVar") { { "Variable", BuildVarName(s) }, { "Value", ConvertState(s) } };
        }

        /// <summary>
        /// Performs the actual task.
        /// </summary>
        protected override void Run()
        {
            // hook up the switch events
            foreach (var s in Configuration.Switch.All)
                s.StateChanged += OnStateChanged;

        NewClient:
            // create the client
            var loggedOn = false;
            try
            {
                using (var client = new AsteriskClient(configuration.BaseUri))
                {
                    // logon to the client
                    client.ExecuteNonQuery(new AsteriskAction("Login") { { "Username", configuration.Username }, { "Secret", configuration.Password } });
                    loggedOn = true;

                    // clear stale updates and sync the switch states
                    lock (updates)
                        updates.Clear();
                    newUpdates.Reset();
                    foreach (var s in Configuration.Switch.All)
                    {
                        // if the switch wasn't set, try to get the old value
                        if (!s.IsDirty)
                        {
                            switch (client.ExecuteScalar(new AsteriskAction("GetVar") { { "Variable", BuildVarName(s) } }, "Value"))
                            {
                                case SwitchOnState:
                                    s.State = Configuration.SwitchState.On;
                                    continue;
                                case SwitchOffState:
                                    s.State = Configuration.SwitchState.Off;
                                    continue;
                            }
                        }

                        // set the new one
                        client.ExecuteNonQuery(SetVar(s));
                    }
                    Program.LogEvent(EventLogEntryType.Information, Properties.Resources.Manager_SyncComplete, configuration.BaseUri);

                    // initialize the async vars and enter the main loop
                    var asyncWaitEvent = client.BeginExecuteEnumeration(new AsteriskAction("WaitEvent"), null, null);
                    var asyncSetVar = (IAsyncResult)null;
                    while (true)
                    {
                        // wait for something to happen
                        var index = WaitHandle.WaitAny(asyncSetVar == null ? new WaitHandle[] { newUpdates, asyncWaitEvent.AsyncWaitHandle } : new WaitHandle[] { newUpdates, asyncWaitEvent.AsyncWaitHandle, asyncSetVar.AsyncWaitHandle });
                        switch (index)
                        {
                            // new switch states are available
                            case 0:
                                // do nothing if we're still syncing
                                if (asyncSetVar != null)
                                    break;

                                // get the next switch
                                Configuration.Switch switchToUpdate;
                                lock (updates)
                                {
                                    // break if there isn't one
                                    if (updates.Count == 0)
                                        break;
                                    switchToUpdate = updates.Dequeue();
                                }

                                // sync the next switch
                                asyncSetVar = client.BeginExecuteNonQuery(SetVar(switchToUpdate), null, null);
                                break;

                            // events have been received
                            case 1:
                                // handle each relay event
                                foreach (var relayEvent in client.EndExecuteEnumeration(asyncWaitEvent).Where(e => string.Equals(e.EventName, "UserEvent", StringComparison.OrdinalIgnoreCase) && string.Equals(e["UserEvent"], "Relay", StringComparison.OrdinalIgnoreCase)))
                                {
                                    // get the switch instance
                                    var switchName = relayEvent["Switch"];
                                    var s = Configuration.Switch.FindByName(switchName);
                                    if (s == null)
                                    {
                                        Program.LogEvent(EventLogEntryType.Warning, Properties.Resources.Manager_UnknownSwitch, configuration.BaseUri, switchName);
                                        continue;
                                    }

                                    // handle each action
                                    var actionName = relayEvent["Action"];
                                    switch (actionName.ToUpperInvariant())
                                    {
                                        case "TURNON":
                                            s.State = Configuration.SwitchState.On;
                                            break;
                                        case "TURNOFF":
                                            s.State = Configuration.SwitchState.Off;
                                            break;
                                        case "TOGGLE":
                                            s.State = ~s.State;
                                            break;
                                        default:
                                            Program.LogEvent(EventLogEntryType.Warning, Properties.Resources.Manager_UnknownAction, configuration.BaseUri, switchName, actionName);
                                            continue;
                                    }
                                }

                                // wait for more events
                                asyncWaitEvent = client.BeginExecuteEnumeration(AsteriskAction.FromIAsyncResult(asyncWaitEvent), null, null);
                                break;

                            // a switch state was synced
                            case 2:
                                // end the operation
                                client.EndExecuteNonQuery(asyncSetVar);
                                asyncSetVar = null;

                                // start with the next
                                goto case 0;
                        }
                    }
                }
            }
            catch (AsteriskException e) { Log(e); if (!loggedOn) Wait(); goto NewClient; }
            catch (WebException e) { Log(e); if (!loggedOn) Wait(); goto NewClient; }
        }

        private void OnStateChanged(object sender, EventArgs e)
        {
            // add the switch to the update set
            lock (updates)
                if (!updates.Contains((Configuration.Switch)sender))
                    updates.Enqueue((Configuration.Switch)sender);
            newUpdates.Set();
        }
    }
}
