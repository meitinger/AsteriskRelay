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
using System.Linq;
using System.ServiceModel;

namespace Aufbauwerk.Asterisk.Relay.Remoting
{
    /// <summary>
    /// Describes the exported service.
    /// </summary>
    [ServiceContract(Name = "relay", Namespace = "http://schemas.aufbauwerk.com/asterisk"), XmlSerializerFormat]
    public interface IService
    {
        /// <summary>
        /// Sets the state of a certain switch.
        /// </summary>
        /// <param name="name">The name of the switch.</param>
        /// <param name="state"><c>true</c> if the switch is to be turned on, <c>false</c> otherwise.</param>
        /// <exception cref="FaultException{ArgumentNullException}">The given <paramref name="name"/> is <c>null</c>.</exception>
        /// <exception cref="FaultException{ArgumentException}">No switch with the given <paramref name="name"/> exists or it's empty.</exception>
        [OperationContract]
        [FaultContract(typeof(ArgumentNullException))]
        [FaultContract(typeof(ArgumentException))]
        void SetSwitchState(string name, bool state);

        /// <summary>
        /// Retrieves the current state of a certain switch.
        /// </summary>
        /// <param name="name">The name of the switch.</param>
        /// <returns><c>true</c> if the switch is turned on, <c>false</c> otherwise.</returns>
        /// <exception cref="FaultException{ArgumentNullException}">The given <paramref name="name"/> is <c>null</c>.</exception>
        /// <exception cref="FaultException{ArgumentException}">No switch with the given <paramref name="name"/> exists or it's empty.</exception>
        [OperationContract]
        [FaultContract(typeof(ArgumentNullException))]
        [FaultContract(typeof(ArgumentException))]
        bool GetSwitchState(string name);

        /// <summary>
        /// Retrieves the names of all available switches.
        /// </summary>
        /// <returns>An array of names.</returns>
        [OperationContract]
        string[] GetSwitchNames();
    }

    /// <summary>
    /// The service implementation.
    /// </summary>
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single, ConcurrencyMode = ConcurrencyMode.Multiple)]
    public class Service : IService
    {
        private static ServiceHost host;

        internal static void Start()
        {
            host = new ServiceHost(new Service());
            Program.RequestAdditionalTime(host.OpenTimeout);
            host.Open();
        }

        internal static void Stop()
        {
            if (host != null)
                host.Abort();
        }

        private Service() { }

        private Configuration.Switch GetSwitch(string name)
        {
            // retrieve the switch with the given name
            if (name == null)
                throw new FaultException<ArgumentNullException>(null, Properties.Resources.Remoting_SwitchNameRequired);
            if (name.Length == 0)
                throw new FaultException<ArgumentException>(null, Properties.Resources.Remoting_SwitchNameRequired);
            var switchOrNull = Configuration.Switch.FindByName(name);
            if (switchOrNull == null)
                throw new FaultException<ArgumentException>(null, string.Format(Properties.Resources.Remoting_SwitchNotFound, name));
            return switchOrNull;
        }

        /// <summary>
        /// Sets the state of a certain switch.
        /// </summary>
        /// <param name="name">The name of the switch.</param>
        /// <param name="state"><c>true</c> if the switch is to be turned on, <c>false</c> otherwise.</param>
        /// <exception cref="FaultException{ArgumentNullException}">The given <paramref name="name"/> is <c>null</c>.</exception>
        /// <exception cref="FaultException{ArgumentException}">No switch with the given <paramref name="name"/> exists or it's empty.</exception>
        public void SetSwitchState(string name, bool state)
        {
            GetSwitch(name).State = state ? Configuration.SwitchState.On : Configuration.SwitchState.Off;
        }

        /// <summary>
        /// Retrieves the current state of a certain switch.
        /// </summary>
        /// <param name="name">The name of the switch.</param>
        /// <returns><c>true</c> if the switch is turned on, <c>false</c> otherwise.</returns>
        /// <exception cref="FaultException{ArgumentNullException}">The given <paramref name="name"/> is <c>null</c>.</exception>
        /// <exception cref="FaultException{ArgumentException}">No switch with the given <paramref name="name"/> exists or it's empty.</exception>
        public bool GetSwitchState(string name)
        {
            return GetSwitch(name).State == Configuration.SwitchState.On;
        }

        /// <summary>
        /// Retrieves the names of all available switches.
        /// </summary>
        /// <returns>An array of names.</returns>
        public string[] GetSwitchNames()
        {
            return Configuration.Switch.All.Select(s => s.Name).ToArray();
        }
    }
}
