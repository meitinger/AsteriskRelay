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
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;

namespace Aufbauwerk.Asterisk.Relay.Hardware
{
    /// <summary>
    /// Static class containing helper functions to issue commands to relay boards.
    /// </summary>
    public static class Command
    {
        private enum OpCode : byte
        {
            NoOp = 0,
            Setup = 1,
            GetPort = 2,
            SetPort = 3
        }

        private const byte X = 0;
        private const int OP = 0;
        private const int ADDR = 1;
        private const int DATA = 2;
        private const int XOR = 3;
        private const int LENGTH = 4;

        private static void Write(SerialPort port, OpCode op, byte address, byte data)
        {
            // discard all buffers
            port.DiscardInBuffer();
            port.DiscardOutBuffer();

            // serialize the command including the checksum
            var buffer = new byte[LENGTH];
            buffer[OP] = (byte)op;
            buffer[ADDR] = address;
            buffer[DATA] = data;
            buffer[XOR] = (byte)((byte)op ^ address ^ data);

            // write the command to the port
            port.Write(buffer, 0, buffer.Length);
        }

        private static byte[] Read(SerialPort port, OpCode op, byte address)
        {
            // read the result
            var buffer = new byte[LENGTH];
            for (var readTotal = 0; readTotal < buffer.Length; readTotal += port.Read(buffer, readTotal, buffer.Length - readTotal)) ;

            // ensure the checksum is valid
            if (buffer[XOR] != (byte)(buffer[OP] ^ buffer[ADDR] ^ buffer[DATA]))
                throw new InvalidDataException(string.Format(Properties.Resources.Hardware_InvalidChecksum, port.PortName, op, address));

            // ensure that the response comes from the right board
            if (address != buffer[ADDR])
                throw new InvalidDataException(string.Format(Properties.Resources.Hardware_DifferentBoardAddress, port.PortName, op, address, buffer[ADDR]));

            // ensure that the repsonse is for the requested command
            var opOut = (byte)~buffer[OP];
            if ((byte)op != opOut && !(op == OpCode.Setup && buffer[OP] == (byte)OpCode.Setup))
                throw new InvalidDataException(string.Format(Properties.Resources.Hardware_DifferentOpCode, port.PortName, op, address, Enum.IsDefined(typeof(OpCode), opOut) ? Enum.GetName(typeof(OpCode), opOut) : string.Format("0x{0:X2}", opOut)));

            // return the buffer
            return buffer;
        }

        private static byte Execute(SerialPort port, OpCode op, byte address, byte data)
        {
            // write the command and read the result
            Write(port, op, address, data);
            return Read(port, op, address)[DATA];
        }

        /// <summary>
        /// NoOp - Ensures that a relay board is still up and running.
        /// </summary>
        /// <param name="port">The serial port used for communication.</param>
        /// <param name="address">The board's address.</param>
        public static void NoOp(SerialPort port, byte address)
        {
            Execute(port, OpCode.NoOp, address, X);
        }

        /// <summary>
        /// Setup - Initializes the relay board(s).
        /// </summary>
        /// <param name="port">The serial port used for communication.</param>
        /// <returns>The number of relay boards connected to the <paramref name="port"/>.</returns>
        public static int Setup(SerialPort port)
        {
            // start the setup and read each board's confirmation
            var address = (byte)1;
            Write(port, OpCode.Setup, address, X);
            while (Read(port, OpCode.Setup, address)[OP] != (byte)OpCode.Setup)
                address++;
            return address - 1;
        }

        /// <summary>
        /// GetPort - Retrieves the state of all relays on a given board.
        /// </summary>
        /// <param name="port">The serial port used for communication.</param>
        /// <param name="address">The board's address.</param>
        /// <returns>A bit-mask where each bit corresponds to a relay on the given port.</returns>
        public static byte GetPort(SerialPort port, byte address)
        {
            return Execute(port, OpCode.GetPort, address, X);
        }

        /// <summary>
        /// SetPort - Adjusts the state of all relays on a given board.
        /// </summary>
        /// <param name="port">The serial port used for communication.</param>
        /// <param name="address">The board's address.</param>
        /// <param name="state">A bit-mask where each bit corresponds to a relay on the given port.</param>
        public static void SetPort(SerialPort port, byte address, byte state)
        {
            Execute(port, OpCode.SetPort, address, state);
        }
    }

    /// <summary>
    /// Class handling all communication with relay boards.
    /// </summary>
    public class Port : BackgroundTask
    {
        private static ICollection<Port> ports;

        internal static void StartAll()
        {
            // create and start all configured ports
            ports = new List<Port>();
            foreach (var portName in Configuration.Board.Ports)
            {
                var port = new Port(portName);
                port.Start();
                ports.Add(port);
            }
        }

        internal static void StopAll()
        {
            // stop all started ports
            if (ports != null)
                foreach (var port in ports)
                    port.Stop();
        }

        private readonly HashSet<Configuration.Board> updates = new HashSet<Configuration.Board>();
        private readonly string name;

        private Port(string name)
            : base(string.Format(Properties.Resources.Hardware_BackgroundTaskName, name), Configuration.SerialPort.Settings.RetryInterval)
        {
            this.name = name;
        }

        private bool UpdateBoardOnPort(SerialPort port, byte address, byte state)
        {
            // try to set the port state, abort after the third failed attempt
            for (var tries = 0; tries < 3; tries++)
            {
                try { Command.SetPort(port, address, state); return true; }
                catch (TimeoutException) { Program.LogEvent(EventLogEntryType.Warning, Properties.Resources.Hardware_OperationTimedOut, port.PortName); }
                catch (InvalidDataException e) { Program.LogEvent(EventLogEntryType.Warning, e.Message); }
                catch (IOException e)
                {
                    // abort immediatelly, since there might be something wrong with the port alltogether
                    Log(e);
                    break;
                }
            }
            return false;
        }

        /// <summary>
        /// Performs the actual task.
        /// </summary>
        protected override void Run()
        {
        NewSerialPort:
            using (var port = new SerialPort())
            {
                // initalize and open the port
                port.PinChanged += OnPinChanged;
                port.PortName = name;
                port.BaudRate = 19200;
                port.Handshake = Handshake.None;
                port.DataBits = 8;
                port.Parity = Parity.None;
                port.StopBits = StopBits.One;
                port.ReadTimeout = Configuration.SerialPort.Settings.ReadTimeout;
                port.WriteTimeout = Configuration.SerialPort.Settings.WriteTimeout;
                while (true)
                {
                    try { port.Open(); break; }
                    catch (IOException e)
                    {
                        Log(e);
                        Wait();
                    }
                }

                // setup the boards and log the result
                int boardsCount;
                for (var retry = 0; ; retry++)
                {
                    try { boardsCount = Command.Setup(port); break; }
                    catch (TimeoutException) { Program.LogEvent(EventLogEntryType.Warning, Properties.Resources.Hardware_OperationTimedOut, port.PortName); }
                    catch (InvalidDataException e) { Program.LogEvent(EventLogEntryType.Warning, e.Message); }
                    catch (IOException e)
                    {
                        // log the error and wait if we've just opened the port
                        Log(e);
                        if (retry == 0)
                            Wait();

                        // try again entirely
                        goto NewSerialPort;
                    }

                    // after three attempts it's likely that either nothing at all or something wrong is connected with this port so wait a bit
                    if (retry > 1)
                        Wait();
                }
                Program.LogEvent(EventLogEntryType.Information, Properties.Resources.Hardware_SetupComplete, name, boardsCount);

                // set the initial state of each board
                for (var i = (byte)1; i <= boardsCount; i++)
                {
                    var board = Configuration.Board.FindByPortAddress(name, i);
                    if (board != null)
                    {
                        board.StateChanged += OnBoardStateChanged;
                        if (!UpdateBoardOnPort(port, board.Address, board.State))
                            goto NewSerialPort;
                    }
                    else
                        Program.LogEvent(EventLogEntryType.Warning, Properties.Resources.Hardware_BoardConfigurationMissing, name, i);
                }

                // log warnings for all additional boards
                foreach (var board in Configuration.Board.All.Where(b => b.Port == name && b.Address > boardsCount))
                    Program.LogEvent(EventLogEntryType.Warning, Properties.Resources.Hardware_BoardAddressOutOfRange, name, board.Address);

                // wait for switch events
                while (true)
                {
                    // make a copy and clear the update list
                    Configuration.Board[] toUpdate;
                    lock (updates)
                    {
                        if (updates.Count == 0)
                            Monitor.Wait(updates);
                        toUpdate = updates.Where(board => board.Address <= boardsCount).ToArray();
                        updates.Clear();
                    }

                    // update each board
                    foreach (var board in toUpdate)
                        if (!UpdateBoardOnPort(port, board.Address, board.State))
                            goto NewSerialPort;
                }
            }
        }

        private void OnPinChanged(object sender, SerialPinChangedEventArgs args)
        {
            // stop the waiting
            if (args.EventType == SerialPinChange.DsrChanged && ((SerialPort)sender).DsrHolding)
                EndWait();
        }

        private void OnBoardStateChanged(object sender, EventArgs args)
        {
            // add it to the updates
            lock (updates)
                if (updates.Add((Configuration.Board)sender))
                    Monitor.Pulse(updates);
        }
    }
}
