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
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using Aufbauwerk.Net.Asterisk;
using Aufbauwerk.ServiceProcess;

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

        internal static void Start(object sender, Aufbauwerk.ServiceProcess.StartEventArgs e)
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

        internal static void Stop(object sender, EventArgs e)
        {
            // stop the manager client
            if (instances != null)
                foreach (var instance in instances)
                    instance.Stop();
        }

        private readonly Queue<Configuration.Switch> updates = new Queue<Configuration.Switch>();
        private readonly ManualResetEvent newUpdates = new ManualResetEvent(false);
        private readonly Configuration.AsteriskManagerInterface configuration;

        private AjamServer(Configuration.AsteriskManagerInterface configuration)
            : base(string.Format(Properties.Resources.Manager_BackgroundTaskName, configuration.BaseUri), configuration.RetryInterval, new Guid("CAC88484-7515-4C03-82E6-71A87ABAC361"))
        {
            this.configuration = configuration;
        }

        private string BuildVarName(Configuration.Switch s)
        {
            return string.Format("DEVSTATE({0})", string.Format(configuration.DeviceNameFormat, s.Name));
        }

        private AsteriskAction SetVar(Configuration.Switch s)
        {
            return new AsteriskAction("SetVar") { { "Variable", BuildVarName(s) }, { "Value", ConvertState(s) } };
        }

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

                    // sync the switch state
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
                    ServiceApplication.LogEvent(EventLogEntryType.Information, Properties.Resources.Manager_SyncComplete, configuration.BaseUri);

                    // initialize the wait handles
                    var waitEventAction = new AsteriskAction("WaitEvent");
                    var waitHandles = new Dictionary<WaitHandle, IAsyncResult>();
                    waitHandles.Add(newUpdates, null);
                    var waitEvent = client.BeginExecuteEnumeration(waitEventAction, null, null);
                    waitHandles.Add(waitEvent.AsyncWaitHandle, waitEvent);
                    while (true)
                    {
                        // wait for something to happen
                        var handles = waitHandles.Keys.ToArray();
                        var index = WaitHandle.WaitAny(handles);
                        var asyncResult = waitHandles[handles[index]];
                        waitHandles.Remove(handles[index]);

                        // new switch states are available
                        if (asyncResult == null)
                        {
                            // get the changed switches and reset the event
                            Configuration.Switch[] changedSwitches;
                            lock (updates)
                            {
                                changedSwitches = updates.ToArray();
                                updates.Clear();
                                newUpdates.Reset();
                            }

                            // start syncing each new switch state
                            foreach (var s in changedSwitches)
                            {
                                var switchUpdate = client.BeginExecuteNonQuery(SetVar(s), null, s);
                                waitHandles.Add(switchUpdate.AsyncWaitHandle, switchUpdate);
                            }

                            // wait for more updates
                            waitHandles.Add(newUpdates, null);
                        }

                        // events have been received
                        else if (asyncResult.AsyncState == null)
                        {
                            // handle each relay event
                            foreach (var relayEvent in client.EndExecuteEnumeration(asyncResult).Where(e => string.Equals(e.EventName, "UserEvent", StringComparison.InvariantCultureIgnoreCase) && string.Equals(e["UserEvent"], "Relay", StringComparison.InvariantCultureIgnoreCase)))
                            {
                                // get the switch instance
                                var switchName = relayEvent["Switch"];
                                var s = Configuration.Switch.FindByName(switchName);
                                if (s == null)
                                {
                                    ServiceApplication.LogEvent(EventLogEntryType.Warning, Properties.Resources.Manager_UnknownSwitch, configuration.BaseUri, switchName);
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
                                        ServiceApplication.LogEvent(EventLogEntryType.Warning, Properties.Resources.Manager_UnknownAction, configuration.BaseUri, switchName, actionName);
                                        continue;
                                }
                            }

                            // wait for more events
                            waitEvent = client.BeginExecuteEnumeration(waitEventAction, null, null);
                            waitHandles.Add(waitEvent.AsyncWaitHandle, waitEvent);
                        }

                        // a switch state was synced
                        else
                            client.EndExecuteNonQuery(asyncResult);
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
            {
                if (!updates.Contains((Configuration.Switch)sender))
                {
                    updates.Enqueue((Configuration.Switch)sender);
                    newUpdates.Set();
                }
            }
        }
    }
}
