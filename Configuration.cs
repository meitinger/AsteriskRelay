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
using System.ComponentModel;
using System.Configuration;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;

namespace Aufbauwerk.Asterisk.Relay.Configuration
{
    /// <summary>
    /// All possible switch states, currently only <c>On</c> and <c>Off</c> are supported.
    /// </summary>
    public enum SwitchState : byte
    {
        Off = 0x00,
        On = 0xFF
    }

    /// <summary>
    /// Base class for all configuration objects.
    /// </summary>
    public abstract class BaseObject
    {
        internal static T Singleton<T>(ref T instanceField, T settingsValue, string errorFormatString) where T : BaseObject, new()
        {
            // set a value if necessary
            if (instanceField == null)
            {
                // create an empty object if there is none
                if (settingsValue == null)
                    settingsValue = new T();

                // ensure that the object is initialized
                try { settingsValue.EnsureInitialized(); }
                catch (ConfigurationErrorsException e) { throw new ConfigurationErrorsException(string.Format(errorFormatString, e.Message)); }

                // store the object if the field is still null
                Interlocked.CompareExchange(ref instanceField, settingsValue, null);
            }
            return instanceField;
        }

        private readonly object initializeLock = new object();
        private bool isInitialized = false;
        private bool isInitalizing = false;

        internal BaseObject() { }

        /// <summary>
        /// Performs the actual initialization within the lock.
        /// </summary>
        protected abstract void PerformInitialization();

        /// <summary>
        /// Initializes the object if it isn't already initialized.
        /// </summary>
        protected internal void EnsureInitialized()
        {
            // initialize the object from the configuration file
            if (!isInitialized)
            {
                lock (initializeLock)
                {
                    if (!isInitialized)
                    {
                        if (isInitalizing)
                            throw new InvalidOperationException(Properties.Resources.Configuration_ReenteredInitializing);
                        isInitalizing = true;
                        try { PerformInitialization(); }
                        finally { isInitalizing = false; }
                        isInitialized = true;
                    }
                }
            }
        }

        /// <summary>
        /// Gets a property after ensuring that the object is initialized.
        /// </summary>
        /// <typeparam name="T">The type of the property to get.</typeparam>
        /// <param name="member">A reference to the member representing the property.</param>
        /// <returns>The property value.</returns>
        protected T Get<T>(ref T member)
        {
            EnsureInitialized();
            return member;
        }

        /// <summary>
        /// Sets a property but throws an exception if the object has already been initialized.
        /// </summary>
        /// <typeparam name="T">The type of the property to set.</typeparam>
        /// <param name="value">The new value of the property.</param>
        /// <param name="member">A reference to the member representing the property.</param>
        /// <exception cref="System.InvalidOperationException">If the object has already been initialized.</exception>
        protected void Set<T>(T value, ref T member)
        {
            lock (initializeLock)
            {
                if (isInitialized)
                    throw new InvalidOperationException(Properties.Resources.Configuration_AlreadyInitialized);
                member = value;
            }
        }
    }

    /// <summary>
    /// Contains the necessary information to connect to an Asterisk manager server.
    /// </summary>
    public sealed class AsteriskManagerInterface : BaseObject
    {
        private static readonly object initLock = new object();
        private static IDictionary<Uri, AsteriskManagerInterface> asteriskManagerInterfaces;

        /// <summary>
        /// Gets all configured Asterisk manager interfaces.
        /// </summary>
        public static IEnumerable<AsteriskManagerInterface> All
        {
            get
            {
                // create the interfaces if necessary
                if (asteriskManagerInterfaces == null)
                {
                    lock (initLock)
                    {
                        if (asteriskManagerInterfaces == null)
                        {
                            // make a copy and ensure that it's not empty
                            var amiArray = Properties.Settings.Default.AsteriskManagerInterfaces;
                            if (amiArray == null)
                                throw new ConfigurationErrorsException(Properties.Resources.Configuration_MissingAsteriskManagerInterfaces);

                            // initialize each manager configuration and check for duplicate base uris
                            var newAsteriskManagerInterfaces = new Dictionary<Uri, AsteriskManagerInterface>();
                            for (var i = 0; i < amiArray.Length; i++)
                            {
                                var ami = amiArray[i];
                                try { ami.EnsureInitialized(); }
                                catch (ConfigurationErrorsException e) { throw new ConfigurationErrorsException(string.Format(Properties.Resources.Configuration_AsteriskManagerInterfaceError, i + 1, e.Message)); }
                                if (newAsteriskManagerInterfaces.ContainsKey(ami.BaseUri))
                                    throw new ConfigurationErrorsException(string.Format(Properties.Resources.Configuration_DuplicateManagerBaseUri, ami.BaseUri));
                                newAsteriskManagerInterfaces.Add(ami.BaseUri, ami);
                            }
                            asteriskManagerInterfaces = newAsteriskManagerInterfaces;
                        }
                    }
                }
                return asteriskManagerInterfaces.Values;
            }
        }

        private string hostname;
        private int port = 8088;
        private string prefix = "/asterisk";
        private string username;
        private string password;
        private string deviceNameFormat;
        private int retryInterval;
        private Uri baseUri;

        protected override void PerformInitialization()
        {
            // check all fields and generate the base uri
            if (string.IsNullOrEmpty(hostname))
                throw new ConfigurationErrorsException(Properties.Resources.Configuration_MissingHostname);
            if (port < 1 || port > ushort.MaxValue)
                throw new ConfigurationErrorsException(Properties.Resources.Configuration_PortOutOfRange);
            if (string.IsNullOrEmpty(username))
                throw new ConfigurationErrorsException(Properties.Resources.Configuration_MissingUsername);
            if (string.IsNullOrEmpty(password))
                throw new ConfigurationErrorsException(Properties.Resources.Configuration_MissingPassword);
            if (string.IsNullOrEmpty(deviceNameFormat))
                throw new ConfigurationErrorsException(Properties.Resources.Configuration_MissingDeviceNameFormat);
            if (retryInterval < 0)
                throw new ConfigurationErrorsException(Properties.Resources.Configuration_RetryIntervalOutOfRange);
            try { baseUri = new UriBuilder("http", hostname, port, prefix).Uri; }
            catch (UriFormatException e) { throw new ConfigurationErrorsException(string.Format(Properties.Resources.Configuration_MalformedUri, e.Message), e); }
        }

        /// <summary>
        /// Gets the server host name.
        /// </summary>
        public string Hostname
        {
            get { return Get(ref hostname); }
            set { Set(value == null ? null : value.Trim(), ref hostname); }
        }

        /// <summary>
        /// Gets the manger port.
        /// </summary>
        public int Port
        {
            get { return Get(ref port); }
            set { Set(value, ref port); }
        }

        /// <summary>
        /// Gets the manager prefix.
        /// </summary>
        public string Prefix
        {
            get { return Get(ref prefix); }
            set { Set(value == null ? null : value.Trim(), ref prefix); }
        }

        /// <summary>
        /// Gets the logon username.
        /// </summary>
        public string Username
        {
            get { return Get(ref username); }
            set { Set(value == null ? null : value.Trim(), ref username); }
        }

        /// <summary>
        /// Gets the logon password.
        /// </summary>
        public string Password
        {
            get { return Get(ref password); }
            set { Set(value == null ? null : value.Trim(), ref password); }
        }

        /// <summary>
        /// Gets the format string to convert a switch name into a device name.
        /// </summary>
        public string DeviceNameFormat
        {
            get { return Get(ref deviceNameFormat); }
            set { Set(value == null ? null : value.Trim(), ref deviceNameFormat); }
        }

        /// <summary>
        /// Gets the amount of time to wait before retrying a failed connection.
        /// </summary>
        public int RetryInterval
        {
            get { return Get(ref retryInterval); }
            set { Set(value, ref retryInterval); }
        }

        /// <summary>
        /// Gets the base URI to the manager interface.
        /// </summary>
        public Uri BaseUri
        {
            get
            {
                EnsureInitialized();
                return baseUri;
            }
        }
    }

    /// <summary>
    /// Configuration class for switches.
    /// </summary>
    public sealed class Switch : BaseObject
    {
        private static readonly object initLock = new object();
        private static IDictionary<string, Switch> switches;

        private static IDictionary<string, Switch> Switches
        {
            get
            {
                // create the switches if necessary
                if (switches == null)
                {
                    lock (initLock)
                    {
                        if (switches == null)
                        {
                            // make a copy and ensure that it's not empty
                            var switchArray = Properties.Settings.Default.Switches;
                            if (switchArray == null)
                                throw new ConfigurationErrorsException(Properties.Resources.Configuration_MissingSwitches);

                            // initialize each switch and check for duplicate names
                            var newSwitches = new SortedDictionary<string, Switch>(StringComparer.InvariantCultureIgnoreCase);
                            for (var i = 0; i < switchArray.Length; i++)
                            {
                                var s = switchArray[i];
                                try { s.EnsureInitialized(); }
                                catch (ConfigurationErrorsException e) { throw new ConfigurationErrorsException(string.Format(Properties.Resources.Configuration_SwitchError, i + 1, e.Message)); }
                                if (newSwitches.ContainsKey(s.Name))
                                    throw new ConfigurationErrorsException(string.Format(Properties.Resources.Configuration_DuplicateSwitchName, s.Name));
                                newSwitches.Add(s.Name, s);
                            }
                            switches = newSwitches;
                        }
                    }
                }
                return switches;
            }
        }

        /// <summary>
        /// Retrieves a switch by its name.
        /// </summary>
        /// <param name="name">The name of the switch to look up.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="name"/> is <c>null</c>.</exception>
        /// <returns>The switch with the given name or <c>null</c>.</returns>
        public static Switch FindByName(string name)
        {
            if (name == null)
                throw new ArgumentNullException("name");
            Switch result;
            Switches.TryGetValue(name, out result);
            return result;
        }

        /// <summary>
        /// Gets all configured switches.
        /// </summary>
        public static IEnumerable<Switch> All
        {
            get { return Switches.Values; }
        }

        private SwitchState state = SwitchState.Off;
        private string name = null;
        private bool performedInit = false;

        protected override void PerformInitialization()
        {
            // ensure that the switch's name was specified
            if (string.IsNullOrEmpty(name))
                throw new ConfigurationErrorsException(Properties.Resources.Configuration_MissingSwitchName);
            performedInit = true;
        }

        internal bool IsDirty { get; private set; }

        /// <summary>
        /// Gets or sets the case-insensitive switch name.
        /// </summary>
        public string Name
        {
            get { return Get(ref name); }
            set { Set(value == null ? null : value.Trim(), ref name); }
        }

        /// <summary>
        /// Gets or sets the switch's on/off state.
        /// </summary>
        public SwitchState State
        {
            get
            {
                EnsureInitialized();
                return state;
            }
            set
            {
                // mark the switch as modified if the state changes after initialization
                if (performedInit)
                    IsDirty = true;

                // handle the state change
                if (state != value)
                {
                    state = value;
                    var stateChanged = StateChanged;
                    if (stateChanged != null)
                        stateChanged(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// Raised when the state was changed.
        /// </summary>
        public event EventHandler StateChanged;
    }

    /// <summary>
    /// Configuration for serial ports.
    /// </summary>
    public sealed class SerialPort : BaseObject
    {
        private static SerialPort settings;

        /// <summary>
        /// Returns the serial port settings.
        /// </summary>
        public static SerialPort Settings
        {
            get { return BaseObject.Singleton(ref settings, Properties.Settings.Default.SerialPort, Properties.Resources.Configuration_SerialPortError); }
        }

        private int readTimeout = System.IO.Ports.SerialPort.InfiniteTimeout;
        private int writeTimeout = System.IO.Ports.SerialPort.InfiniteTimeout;
        private int retryInterval = 0;

        protected override void PerformInitialization()
        {
            // ensure that the timeouts are greater than one or infinite
            if (readTimeout <= 0 && readTimeout != System.IO.Ports.SerialPort.InfiniteTimeout)
                throw new ConfigurationErrorsException(Properties.Resources.Configuration_ReadTimeoutOutOfRange);
            if (writeTimeout <= 0 && writeTimeout != System.IO.Ports.SerialPort.InfiniteTimeout)
                throw new ConfigurationErrorsException(Properties.Resources.Configuration_WriteTimeoutOutOfRange);
            if (retryInterval < 0)
                throw new ConfigurationErrorsException(Properties.Resources.Configuration_RetryIntervalOutOfRange);
        }

        /// <summary>
        /// Gets the time-out for read operations in milliseconds.
        /// </summary>
        public int ReadTimeout
        {
            get { return Get(ref readTimeout); }
            set { Set(value, ref readTimeout); }
        }

        /// <summary>
        /// Gets the time-out for write operations in milliseconds.
        /// </summary>
        public int WriteTimeout
        {
            get { return Get(ref writeTimeout); }
            set { Set(value, ref writeTimeout); }
        }

        /// <summary>
        /// Gets the time span to wait before retrying a failed COM port.
        /// </summary>
        public int RetryInterval
        {
            get { return Get(ref retryInterval); }
            set { Set(value, ref retryInterval); }
        }
    }

    /// <summary>
    /// Configuration class for Conrad relay boards.
    /// </summary>
    public sealed class Board : BaseObject
    {
        private static readonly object initLock = new object();
        private static IDictionary<string, IDictionary<byte, Board>> boards;

        private static IDictionary<string, IDictionary<byte, Board>> Boards
        {
            get
            {
                if (boards == null)
                {
                    lock (initLock)
                    {
                        if (boards == null)
                        {
                            // make a copy and ensure that it's not empty
                            var boardArray = Properties.Settings.Default.Boards;
                            if (boardArray == null)
                                throw new ConfigurationErrorsException(Properties.Resources.Configuration_MissingBoards);

                            // initialize each board and check for duplicate port/address pairs
                            var newBoards = new Dictionary<string, IDictionary<byte, Board>>(StringComparer.InvariantCultureIgnoreCase);
                            for (var i = 0; i < boardArray.Length; i++)
                            {
                                var b = boardArray[i];
                                try { b.EnsureInitialized(); }
                                catch (ConfigurationErrorsException e) { throw new ConfigurationErrorsException(string.Format(Properties.Resources.Configuration_BoardError, i + 1, e.Message)); }
                                IDictionary<byte, Board> boardMap;
                                if (!newBoards.TryGetValue(b.Port, out boardMap))
                                    newBoards.Add(b.Port, boardMap = new Dictionary<byte, Board>());
                                if (boardMap.ContainsKey(b.Address))
                                    throw new ConfigurationErrorsException(string.Format(Properties.Resources.Configuration_DuplicateBoardAdddress, b.Port, b.Address));
                                boardMap.Add(b.Address, b);
                            }
                            boards = newBoards;
                        }
                    }
                }
                return boards;
            }
        }

        /// <summary>
        /// Gets all used port names in upper-case.
        /// </summary>
        public static IEnumerable<string> Ports
        {
            get { return Boards.Keys; }
        }

        /// <summary>
        /// Retrieves the board on a certain port and position.
        /// </summary>
        /// <param name="port">The name of the communication port.</param>
        /// <param name="address">The position of the board within the daisy chain.</param>
        /// <exception cref="System.ArgumentNullException"><paramref name="port"/> is <c>null</c>.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="address"/> is <c>0</c>.</exception>
        /// <returns>The matching board or <c>null</c> if there isn't one.</returns>
        public static Board FindByPortAddress(string port, byte address)
        {
            if (port == null)
                throw new ArgumentNullException("port");
            if (address == 0)
                throw new ArgumentOutOfRangeException("address");
            IDictionary<byte, Board> dict;
            if (!Boards.TryGetValue(port, out dict))
                return null;
            Board board;
            dict.TryGetValue(address, out board);
            return board;
        }

        /// <summary>
        /// Gets all configured board objects.
        /// </summary>
        public static IEnumerable<Board> All
        {
            get { return Boards.Values.Aggregate(Enumerable.Empty<Board>(), (acc, dict) => acc.Concat(dict.Values)); }
        }

        private readonly Relay[] relays = new Relay[8];
        private string port;
        private byte address;
        private byte state;

        private void UpdateState(EventArgs e)
        {
            // calculate the new state
            var newState = 0;
            lock (relays)
                for (var i = 0; i < relays.Length; i++)
                    if (relays[i].Value)
                        newState |= 1 << i;
            state = (byte)newState;

            // notify listeners of a change in a thread-safe manner
            var stateChanged = StateChanged;
            if (stateChanged != null)
                stateChanged(this, e);
        }

        protected override void PerformInitialization()
        {
            // ensure that the port name and board address have been specified
            if (string.IsNullOrEmpty(port))
                throw new ConfigurationErrorsException(Properties.Resources.Configuration_MissingPortName);
            if (address == 0)
                throw new ConfigurationErrorsException(Properties.Resources.Configuration_InvalidBoardAddress);

            // initialize each relay
            for (var i = 0; i < relays.Length; i++)
            {
                var r = relays[i];
                if (r == null)
                    throw new ConfigurationErrorsException(string.Format(Properties.Resources.Configuration_MissingRelay, i + 1));
                try { r.EnsureInitialized(); }
                catch (ConfigurationErrorsException e) { throw new ConfigurationErrorsException(string.Format(Properties.Resources.Configuration_RelayError, i + 1, e.Message)); }
                r.ValueChanged += (s, e) => UpdateState(e);
            }

            // set the state
            UpdateState(EventArgs.Empty);
        }

        /// <summary>
        /// Gets or sets the port name for communications.
        /// </summary>
        public string Port
        {
            get { return Get(ref port); }
            set { Set(value == null ? null : value.Trim().ToUpperInvariant(), ref port); }
        }

        /// <summary>
        /// Gets or sets the board's daisy chain position.
        /// </summary>
        public byte Address
        {
            get { return Get(ref address); }
            set { Set(value, ref address); }
        }

        /// <summary>
        /// Gets the bit-field converted state of all relays.
        /// </summary>
        public byte State
        {
            get
            {
                EnsureInitialized();
                return state;
            }
        }

        /// <summary>
        /// Raised when the value of a relay is changed.
        /// </summary>
        public event EventHandler StateChanged;

        #region Relays 1-8

        /// <summary>
        /// Function to calculate the state of relay #1.
        /// </summary>
        public Relay Relay1
        {
            get { return Get(ref relays[0]); }
            set { Set(value, ref relays[0]); }
        }

        /// <summary>
        /// Function to calculate the state of relay #2.
        /// </summary>
        public Relay Relay2
        {
            get { return Get(ref relays[1]); }
            set { Set(value, ref relays[1]); }
        }

        /// <summary>
        /// Function to calculate the state of relay #3.
        /// </summary>
        public Relay Relay3
        {
            get { return Get(ref relays[2]); }
            set { Set(value, ref relays[2]); }
        }

        /// <summary>
        /// Function to calculate the state of relay #4.
        /// </summary>
        public Relay Relay4
        {
            get { return Get(ref relays[3]); }
            set { Set(value, ref relays[3]); }
        }

        /// <summary>
        /// Function to calculate the state of relay #5.
        /// </summary>
        public Relay Relay5
        {
            get { return Get(ref relays[4]); }
            set { Set(value, ref relays[4]); }
        }

        /// <summary>
        /// Function to calculate the state of relay #6.
        /// </summary>
        public Relay Relay6
        {
            get { return Get(ref relays[5]); }
            set { Set(value, ref relays[5]); }
        }

        /// <summary>
        /// Function to calculate the state of relay #7.
        /// </summary>
        public Relay Relay7
        {
            get { return Get(ref relays[6]); }
            set { Set(value, ref relays[6]); }
        }

        /// <summary>
        /// Function to calculate the state of relay #8.
        /// </summary>
        public Relay Relay8
        {
            get { return Get(ref relays[7]); }
            set { Set(value, ref relays[7]); }
        }

        #endregion
    }

    /// <summary>
    /// Top-level class containing the entire relay function.
    /// </summary>
    public sealed class Relay : UnaryFunction
    {
        public Relay() : base(_ => _) { }
    }

    /// <summary>
    /// Base class for all logic functions.
    /// </summary>
    public abstract class Function : BaseObject
    {
        internal Function() { }

        /// <summary>
        /// Notifies all listeners of a change in the function's value.
        /// </summary>
        /// <param name="args">Additional event arguments.</param>
        protected void OnValueChanged(EventArgs args)
        {
            var change = ValueChanged;
            if (change != null)
                change(this, args);
        }

        /// <summary>
        /// Evaluates the function and returns its result.
        /// </summary>
        [XmlIgnore]
        public abstract bool Value { get; }

        /// <summary>
        /// Fires whenever the value of the function changes.
        /// </summary>
        public event EventHandler ValueChanged;
    }

    /// <summary>
    /// Base class for all functions with a variable number of arguments.
    /// </summary>
    public abstract class VariadicFunction : Function
    {
        private readonly Func<bool, bool, bool> method;
        private IEnumerable<Function> ops = null;

        internal VariadicFunction(Func<bool, bool, bool> method)
        {
            this.method = method;
        }

        protected override void PerformInitialization()
        {
            // ensure that both operators are specified, valid and hooked
            if (ops == null || ops.Count() == 0)
                throw new ConfigurationErrorsException(Properties.Resources.Configuration_MissingOperand);
            foreach (var op in ops)
            {
                try { op.EnsureInitialized(); }
                catch (ConfigurationErrorsException e) { throw new ConfigurationErrorsException(string.Format(Properties.Resources.Configuration_OperandError, op.GetType().Name, e.Message)); }
                op.ValueChanged += (s, e) => OnValueChanged(e);
            }
        }

        [XmlElement("Equals", typeof(Equals))]
        [XmlElement("And", typeof(And))]
        [XmlElement("Or", typeof(Or))]
        [XmlElement("Xor", typeof(Xor))]
        [XmlElement("Not", typeof(Not))]
        [XmlElement("AlwaysOn", typeof(AlwaysOn))]
        [XmlElement("AlwaysOff", typeof(AlwaysOff))]
        [XmlElement("SwitchRef", typeof(SwitchRef))]
        [EditorBrowsable(EditorBrowsableState.Never), Browsable(false)]
        public object[] InternalOps
        {
            get { return Ops.ToArray(); }
            set { Ops = value.Cast<Function>(); }
        }

        /// <summary>
        /// All operands.
        /// </summary>
        [XmlIgnore]
        public IEnumerable<Function> Ops
        {
            get { return Get(ref ops); }
            set { Set(value, ref ops); }
        }

        public override bool Value
        {
            get
            {
                EnsureInitialized();
                return ops.Aggregate(ops.First().Value, (v, m) => method(v, m.Value));
            }
        }
    }

    /// <summary>
    /// Base class for all unary functions.
    /// </summary>
    public abstract class UnaryFunction : Function
    {
        private readonly Func<bool, bool> method;
        private Function op = null;

        internal UnaryFunction(Func<bool, bool> method)
        {
            this.method = method;
        }

        protected override void PerformInitialization()
        {
            // ensure that the operation is present, valid and hooked
            if (op == null)
                throw new ConfigurationErrorsException(Properties.Resources.Configuration_MissingOperand);
            try { op.EnsureInitialized(); }
            catch (ConfigurationErrorsException e) { throw new ConfigurationErrorsException(string.Format(Properties.Resources.Configuration_OperandError, op.GetType().Name, e.Message)); }
            op.ValueChanged += (s, e) => OnValueChanged(e);
        }

        [XmlElement("Equals", typeof(Equals))]
        [XmlElement("And", typeof(And))]
        [XmlElement("Or", typeof(Or))]
        [XmlElement("Xor", typeof(Xor))]
        [XmlElement("Not", typeof(Not))]
        [XmlElement("AlwaysOn", typeof(AlwaysOn))]
        [XmlElement("AlwaysOff", typeof(AlwaysOff))]
        [XmlElement("SwitchRef", typeof(SwitchRef))]
        [EditorBrowsable(EditorBrowsableState.Never), Browsable(false)]
        public object InternalOp
        {
            get { return Op; }
            set { Op = (Function)value; }
        }

        /// <summary>
        /// Operand.
        /// </summary>
        [XmlIgnore]
        public Function Op
        {
            get { return Get(ref op); }
            set { Set(value, ref op); }
        }

        public override bool Value
        {
            get
            {
                EnsureInitialized();
                return method(op.Value);
            }
        }
    }

    /// <summary>
    /// Base class for constant values.
    /// </summary>
    public abstract class ConstantFunction : Function
    {
        private readonly bool value;

        internal ConstantFunction(bool value)
        {
            this.value = value;
        }

        protected override void PerformInitialization() { }

        public override bool Value
        {
            get
            {
                EnsureInitialized();
                return value;
            }
        }
    }

    /// <summary>
    /// Returns the result of <c>==</c>.
    /// </summary>
    public sealed class Equals : VariadicFunction
    {
        public Equals() : base((op1, op2) => op1 == op2) { }
    }

    /// <summary>
    /// Returns the result of <c>&</c>.
    /// </summary>
    public sealed class And : VariadicFunction
    {
        public And() : base((op1, op2) => op1 & op2) { }
    }

    /// <summary>
    /// Returns the result of <c>|</c>.
    /// </summary>
    public sealed class Or : VariadicFunction
    {
        public Or() : base((op1, op2) => op1 | op2) { }
    }

    /// <summary>
    /// Returns the result of <c>^</c>.
    /// </summary>
    public sealed class Xor : VariadicFunction
    {
        public Xor() : base((op1, op2) => op1 ^ op2) { }
    }

    /// <summary>
    /// Returns the opposite of another function.
    /// </summary>
    public sealed class Not : UnaryFunction
    {
        public Not() : base(op => !op) { }
    }

    /// <summary>
    /// Always returns <c>true</c>.
    /// </summary>
    public sealed class AlwaysOn : ConstantFunction
    {
        public AlwaysOn() : base(true) { }
    }

    /// <summary>
    /// Always returns <c>false</c>.
    /// </summary>
    public sealed class AlwaysOff : ConstantFunction
    {
        public AlwaysOff() : base(false) { }
    }

    /// <summary>
    /// Returns the value of a given switch.
    /// </summary>
    public sealed class SwitchRef : Function
    {
        private string name = null;
        private Switch switchRef;

        protected override void PerformInitialization()
        {
            // ensure that the switch reference exists and hook up its state changed event
            if (string.IsNullOrEmpty(name))
                throw new ConfigurationErrorsException(Properties.Resources.Configuration_MissingSwitchName);
            switchRef = Switch.FindByName(name);
            if (switchRef == null)
                throw new ConfigurationErrorsException(string.Format(Properties.Resources.Configuration_InvalidSwitchName, name));
            switchRef.StateChanged += (s, e) => OnValueChanged(e);
        }

        /// <summary>
        /// The name of the referenced switch.
        /// </summary>
        [XmlText]
        public string Name
        {
            get { return Get(ref name); }
            set { Set(value == null ? null : value.Trim(), ref name); }
        }

        public override bool Value
        {
            get
            {
                EnsureInitialized();
                return switchRef.State == SwitchState.On;
            }
        }
    }
}
