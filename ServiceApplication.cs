/* Copyright (C) 2012-2013, Manuel Meitinger
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
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace Aufbauwerk.ServiceProcess
{
    #region NetworkBindingChanged classes

    /// <summary>
    /// Specifies the reason for a network binding change notice.
    /// </summary>
    public enum NetworkBindingChangedReason
    {
        /// <summary>
        /// Notifies a network service that there is a new component for binding.
        /// The service should bind to the new component.
        /// </summary>
        Add,

        /// <summary>
        /// Notifies a network service that a component for binding has been removed.
        /// The service should reread its binding information and unbind from the removed component.
        /// </summary>
        Remove,

        /// <summary>
        /// Notifies a network service that a disabled binding has been enabled.
        /// The service should reread its binding information and add the new binding.
        /// </summary>
        Enable,

        /// <summary>
        /// Notifies a network service that one of its bindings has been disabled.
        /// The service should reread its binding information and remove the binding.
        /// </summary>
        Disable,
    }

    /// <summary>
    /// Provides data for the <see cref="ServiceApplication.NetworkBindingChanged"/> event.
    /// </summary>
    public sealed class NetworkBindingChangedEventArgs : EventArgs
    {
        public NetworkBindingChangedEventArgs(NetworkBindingChangedReason reason)
        {
            ServiceApplication.CheckEnum("reason", reason);
            Reason = reason;
        }

        /// <summary>
        /// Gets the reason for a network binding change.
        /// </summary>
        public NetworkBindingChangedReason Reason { get; private set; }
    }

    #endregion

    #region DeviceEvent classes

    /// <summary>
    /// Specifies the type of <see cref="ServiceApplication.DeviceEvent"/> event.
    /// </summary>
    public enum DeviceEventType
    {
        /// <summary>
        /// A device or piece of media has been inserted and becomes available.
        /// </summary>
        Arrival = 0x8000,

        /// <summary>
        /// A device or piece of media has been physically removed.
        /// </summary>
        RemoveComplete = 0x8004,

        /// <summary>
        /// Request permission to remove a device or piece of media.
        /// </summary>
        QueryRemove = 0x8001,

        /// <summary>
        /// Request to remove a device or piece of media has been canceled.
        /// </summary>
        QueryRemoveFailed = 0x8002,

        /// <summary>
        /// A device or piece of media is being removed and is no longer available for use.
        /// </summary>
        RemovePending = 0x8003,

        /// <summary>
        /// A driver-defined custom event has occurred.
        /// </summary>
        CustomEvent = 0x8006,
    }

    /// <summary>
    /// Specifies the type of device for <see cref="ServiceApplication.DeviceEvent"/> events.
    /// </summary>
    public enum DeviceEventDeviceType
    {
        /// <summary>
        /// Unknown or not implemented device type.
        /// </summary>
        Unknown = -1,

        /// <summary>
        /// Class of devices.
        /// </summary>
        Interface = 0x00000005,

        /// <summary>
        /// File system handle.
        /// </summary>
        Handle = 0x00000006,

        /// <summary>
        /// OEM- or IHV-defined device type.
        /// </summary>
        Oem = 0x00000000,

        /// <summary>
        /// Port device (serial or parallel).
        /// </summary>
        Port = 0x00000003,

        /// <summary>
        /// Logical volume.
        /// </summary>
        Volume = 0x00000002,
    }

    /// <summary>
    /// Provides data for the <see cref="ServiceApplication.DeviceEvent"/> event.
    /// </summary>
    public abstract class DeviceEventArgs : CancelEventArgs
    {
        #region Win32

        private const ushort DBTF_MEDIA = 0x0001;
        private const ushort DBTF_NET = 0x0002;

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        private static extern void CopyMemory([In] IntPtr destination, [In] IntPtr source, [In] int length);

        [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
        private sealed class OpenArrayAttribute : Attribute { }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEV_BROADCAST_HDR
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public uint dbcc_reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEV_BROADCAST_DEVICEINTERFACE
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public uint dbcc_reserved;
            public Guid dbcc_classguid;
            [OpenArray, MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public char[] dbcc_name;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEV_BROADCAST_HANDLE
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public uint dbcc_reserved;
            public IntPtr dbch_handle;
            public IntPtr dbch_hdevnotify;
            public Guid dbch_eventguid;
            public int dbch_nameoffset;
            [OpenArray, MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public byte[] dbch_data;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEV_BROADCAST_OEM
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public uint dbcc_reserved;
            public uint dbco_identifier;
            public uint dbco_suppfunc;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEV_BROADCAST_VOLUME
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public uint dbcc_reserved;
            public uint dbcv_unitmask;
            public ushort dbcv_flags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEV_BROADCAST_PORT
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public uint dbcc_reserved;
            [OpenArray, MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public char[] dbcp_name;
        }

        #endregion

        private static DeviceEventArgs CreateInternal<T>(IntPtr buffer, int bufferSize, Converter<T, DeviceEventArgs> convert) where T : struct
        {
            // ensure that the buffer is large enough
            var typeSize = Marshal.SizeOf(typeof(T));
            var sizeDifference = bufferSize - typeSize;
            if (sizeDifference < 0)
                throw new ArgumentOutOfRangeException("bufferSize");

            // read the buffer
            var structure = (T)Marshal.PtrToStructure(buffer, typeof(T));
            if (sizeDifference > 0)
            {
                // the buffer is larger, read all elements of an open array if there is one (otherwise the structure might be newer)
                var openArrayField = (from field in typeof(T).GetFields() where field.GetCustomAttributes(typeof(OpenArrayAttribute), true).Length > 0 select field).SingleOrDefault();
                if (openArrayField != null)
                {
                    // ensure that the additional size is a multiple of the array type
                    var elementType = openArrayField.FieldType.GetElementType();
                    var elementSize = elementType == typeof(char) ? 2 : Marshal.SizeOf(elementType);
                    if (sizeDifference % elementSize != 0)
                        throw new ArgumentOutOfRangeException("bufferSize");

                    // create and copy all array elements (from the offset of the field not the end, since there might be padding)
                    var array = Array.CreateInstance(elementType, sizeDifference / elementSize + 1);
                    var handle = GCHandle.Alloc(array, GCHandleType.Pinned);
                    try { CopyMemory(Marshal.UnsafeAddrOfPinnedArrayElement(array, 0), new IntPtr(buffer.ToInt64() + Marshal.OffsetOf(typeof(T), openArrayField.Name).ToInt32()), elementSize + sizeDifference); }
                    finally { handle.Free(); }
                    object boxed = structure;
                    openArrayField.SetValue(boxed, array);
                    structure = (T)boxed;
                }
            }

            // convert the structure and return the result
            return convert(structure);
        }

        internal static DeviceEventArgs FromNative(int eventType, IntPtr eventData)
        {
            // check the input data
            if (!Enum.IsDefined(typeof(DeviceEventType), eventType))
                return null;
            var type = (DeviceEventType)eventType;
            if (eventData == IntPtr.Zero)
                return new UnknownDeviceEventArgs(type);
            var header = (DEV_BROADCAST_HDR)Marshal.PtrToStructure(eventData, typeof(DEV_BROADCAST_HDR));

            // handle each device type
            switch ((DeviceEventDeviceType)header.dbcc_devicetype)
            {
                case DeviceEventDeviceType.Interface:
                    return CreateInternal<DEV_BROADCAST_DEVICEINTERFACE>
                    (
                        eventData, header.dbcc_size,
                        device => new InterfaceDeviceEventArgs(type, device.dbcc_classguid, new string(device.dbcc_name, 0, device.dbcc_name.Length - 1))
                    );
                case DeviceEventDeviceType.Handle:
                    return CreateInternal<DEV_BROADCAST_HANDLE>
                    (
                        eventData, header.dbcc_size,
                        handle => new HandleDeviceEventArgs
                        (
                            type,
                            handle.dbch_handle,
                            type == DeviceEventType.CustomEvent ? handle.dbch_eventguid : Guid.Empty,
                            type == DeviceEventType.CustomEvent && handle.dbch_nameoffset > 0 ? Marshal.PtrToStringUni(new IntPtr(eventData.ToInt64() + handle.dbch_nameoffset)) : null,
                            type == DeviceEventType.CustomEvent ? handle.dbch_data : null
                        )
                    );
                case DeviceEventDeviceType.Oem:
                    return CreateInternal<DEV_BROADCAST_OEM>
                    (
                        eventData, header.dbcc_size,
                        oem => new OemDeviceEventArgs(type, oem.dbco_identifier, oem.dbco_suppfunc)
                    );
                case DeviceEventDeviceType.Port:
                    return CreateInternal<DEV_BROADCAST_PORT>
                    (
                        eventData, header.dbcc_size,
                        port => new PortDeviceEventArgs(type, new string(port.dbcp_name, 0, port.dbcp_name.Length - 1))
                    );
                case DeviceEventDeviceType.Volume:
                    return CreateInternal<DEV_BROADCAST_VOLUME>
                    (
                        eventData, header.dbcc_size,
                        volume => new VolumeDeviceEventArgs
                        (
                            type,
                            volume.dbcv_unitmask,
                            (volume.dbcv_flags & DBTF_MEDIA) != 0,
                            (volume.dbcv_flags & DBTF_NET) != 0
                        )
                    );
                default:
                    return new UnknownDeviceEventArgs(type);
            }
        }

#if DEBUG
        internal bool ToNative(out int eventType, out IntPtr eventData)
        {
            // store the event type and create the the corresponding native structure
            eventType = (int)Type;
            int size;
            object data;
            switch (DeviceType)
            {
                case DeviceEventDeviceType.Interface:
                    data = new DEV_BROADCAST_DEVICEINTERFACE()
                    {
                        dbcc_size = size = Marshal.SizeOf(typeof(DEV_BROADCAST_DEVICEINTERFACE)) + ((InterfaceDeviceEventArgs)this).Name.Length * 2,
                        dbcc_devicetype = (int)DeviceType,
                        dbcc_reserved = 0,
                        dbcc_classguid = ((InterfaceDeviceEventArgs)this).ClassGuid,
                        dbcc_name = (((InterfaceDeviceEventArgs)this).Name + '\0').ToCharArray(),
                    };
                    break;
                case DeviceEventDeviceType.Handle:
                    data = new DEV_BROADCAST_HANDLE()
                    {
                        dbcc_size = size = Marshal.SizeOf(typeof(DEV_BROADCAST_HANDLE)) +
                                           (((HandleDeviceEventArgs)this).Data == null || ((HandleDeviceEventArgs)this).Data.Length == 0 ? 0 : (((HandleDeviceEventArgs)this).Data.Length - 1)) +
                                           (((HandleDeviceEventArgs)this).Name == null ? 0 : (((HandleDeviceEventArgs)this).Name.Length + 1) * 2),
                        dbcc_devicetype = (int)DeviceType,
                        dbcc_reserved = 0,
                        dbch_handle = ((HandleDeviceEventArgs)this).Handle,
                        dbch_hdevnotify = IntPtr.Zero,
                        dbch_eventguid = ((HandleDeviceEventArgs)this).EventGuid,
                        dbch_nameoffset = ((HandleDeviceEventArgs)this).Name == null ? 0 : (Marshal.OffsetOf(typeof(DEV_BROADCAST_HANDLE), "dbch_data").ToInt32() + (((HandleDeviceEventArgs)this).Data == null || ((HandleDeviceEventArgs)this).Data.Length == 0 ? 1 : ((HandleDeviceEventArgs)this).Data.Length)),
                        dbch_data = (((HandleDeviceEventArgs)this).Data == null || ((HandleDeviceEventArgs)this).Data.Length == 0 ? new byte[1] { 0x00 } : ((HandleDeviceEventArgs)this).Data).Concat(((HandleDeviceEventArgs)this).Name == null ? new byte[0] : System.Text.Encoding.Unicode.GetBytes(((HandleDeviceEventArgs)this).Name + '\0')).ToArray(),
                    };
                    break;
                case DeviceEventDeviceType.Oem:
                    data = new DEV_BROADCAST_OEM()
                    {
                        dbcc_size = size = Marshal.SizeOf(typeof(DEV_BROADCAST_OEM)),
                        dbcc_devicetype = (int)DeviceType,
                        dbcc_reserved = 0,
                        dbco_identifier = ((OemDeviceEventArgs)this).Identifier,
                        dbco_suppfunc = ((OemDeviceEventArgs)this).SuppliedFunction,
                    };
                    break;
                case DeviceEventDeviceType.Port:
                    data = new DEV_BROADCAST_PORT()
                    {
                        dbcc_size = size = Marshal.SizeOf(typeof(DEV_BROADCAST_PORT)) + ((PortDeviceEventArgs)this).Name.Length * 2,
                        dbcc_devicetype = (int)DeviceType,
                        dbcc_reserved = 0,
                        dbcp_name = (((PortDeviceEventArgs)this).Name + '\0').ToCharArray(),
                    };
                    break;
                case DeviceEventDeviceType.Volume:
                    data = new DEV_BROADCAST_VOLUME()
                    {
                        dbcc_size = size = Marshal.SizeOf(typeof(DEV_BROADCAST_VOLUME)),
                        dbcc_devicetype = (int)DeviceType,
                        dbcc_reserved = 0,
                        dbcv_unitmask = ((VolumeDeviceEventArgs)this).UnitMask,
                        dbcv_flags = (ushort)((((VolumeDeviceEventArgs)this).IsMediaEvent ? DBTF_MEDIA : 0) | (((VolumeDeviceEventArgs)this).IsNetworkVolume ? DBTF_NET : 0)),
                    };
                    break;
                default:
                    eventData = IntPtr.Zero;
                    return false;
            }

            // allocate the memory and fill it
            eventData = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(data, eventData, false);

            // if there isn't any additional data, we're done
            var type = data.GetType();
            var actualSize = Marshal.SizeOf(type);
            if (actualSize == size)
                return true;

            // store the additional data
            var openArrayField = (from field in type.GetFields() where field.GetCustomAttributes(typeof(OpenArrayAttribute), true).Length > 0 select field).Single();
            var array = (Array)openArrayField.GetValue(data);
            var elementType = openArrayField.FieldType.GetElementType();
            var handle = GCHandle.Alloc(array, GCHandleType.Pinned);
            try { CopyMemory(new IntPtr(eventData.ToInt64() + Marshal.OffsetOf(type, openArrayField.Name).ToInt32()), Marshal.UnsafeAddrOfPinnedArrayElement(array, 0), (elementType == typeof(char) ? 2 : Marshal.SizeOf(elementType)) * array.Length); }
            finally { handle.Free(); }
            return true;
        }
#endif

        internal DeviceEventArgs(DeviceEventType type, DeviceEventDeviceType deviceType)
        {
            ServiceApplication.CheckEnum("type", type);
            ServiceApplication.CheckEnum("deviceType", deviceType);
            Type = type;
            DeviceType = deviceType;
        }

        /// <summary>
        /// Gets the type of event.
        /// </summary>
        public DeviceEventType Type { get; private set; }

        /// <summary>
        /// Gets the type of device.
        /// </summary>
        public DeviceEventDeviceType DeviceType { get; private set; }

        public override bool CanBeCanceled
        {
            get { return Type == DeviceEventType.QueryRemove; }
        }
    }

    /// <summary>
    /// Contains only the type of the event and will be returned if the device information couldn't be gathered.
    /// </summary>
    public sealed class UnknownDeviceEventArgs : DeviceEventArgs
    {
        public UnknownDeviceEventArgs(DeviceEventType type) : base(type, DeviceEventDeviceType.Unknown) { }
    }

    /// <summary>
    /// Contains information about a class of devices.
    /// </summary>
    public sealed class InterfaceDeviceEventArgs : DeviceEventArgs
    {
        public InterfaceDeviceEventArgs(DeviceEventType type, Guid classGuid, string name)
            : base(type, DeviceEventDeviceType.Interface)
        {
            if (name == null)
                throw new ArgumentNullException("name");
            ClassGuid = classGuid;
            Name = name;
        }

        /// <summary>
        /// The GUID for the interface device class.
        /// </summary>
        public Guid ClassGuid { get; private set; }

        /// <summary>
        /// A string that specifies the name of the device.
        /// </summary>
        public string Name { get; private set; }
    }

    /// <summary>
    /// Contains information about a file system handle.
    /// </summary>
    public sealed class HandleDeviceEventArgs : DeviceEventArgs
    {
        public HandleDeviceEventArgs(DeviceEventType type, IntPtr handle, Guid eventGuid, string name, byte[] data)
            : base(type, DeviceEventDeviceType.Handle)
        {
            if (handle == IntPtr.Zero)
                throw new ArgumentNullException("handle");
            Handle = handle;
            EventGuid = eventGuid;
            Name = name;
            Data = data;
        }

        /// <summary>
        /// A handle to the device to be checked.
        /// </summary>
        public IntPtr Handle { get; private set; }

        /// <summary>
        /// The GUID for the custom event.
        /// </summary>
        public Guid EventGuid { get; private set; }

        /// <summary>
        /// Optional string buffer.
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// Optional binary data.
        /// </summary>
        public byte[] Data { get; private set; }
    }

    /// <summary>
    /// Contains information about an OEM-defined device type.
    /// </summary>
    public sealed class OemDeviceEventArgs : DeviceEventArgs
    {
        public OemDeviceEventArgs(DeviceEventType type, uint identifier, uint suppliedFunction)
            : base(type, DeviceEventDeviceType.Oem)
        {
            Identifier = identifier;
            SuppliedFunction = suppliedFunction;
        }

        /// <summary>
        /// The OEM-specific identifier for the device.
        /// </summary>
        public uint Identifier { get; private set; }

        /// <summary>
        /// The OEM-specific function value. Possible values depend on the device.
        /// </summary>
        public uint SuppliedFunction { get; private set; }
    }

    /// <summary>
    /// Contains information about a modem, serial, or parallel port.
    /// </summary>
    public sealed class PortDeviceEventArgs : DeviceEventArgs
    {
        public PortDeviceEventArgs(DeviceEventType type, string name)
            : base(type, DeviceEventDeviceType.Port)
        {
            if (name == null)
                throw new ArgumentNullException("name");
            Name = name;
        }

        /// <summary>
        /// A string specifying the friendly name of the port or the device connected to the port.
        /// </summary>
        public string Name { get; private set; }
    }

    /// <summary>
    /// Contains information about a logical volume.
    /// </summary>
    public sealed class VolumeDeviceEventArgs : DeviceEventArgs
    {
        public VolumeDeviceEventArgs(DeviceEventType type, uint unitMask, bool isMediaEvent, bool isNetworkEvent)
            : base(type, DeviceEventDeviceType.Volume)
        {
            UnitMask = unitMask;
            IsMediaEvent = isMediaEvent;
            IsNetworkVolume = isNetworkEvent;
        }

        /// <summary>
        /// The logical unit mask identifying one or more logical units.
        /// Each bit in the mask corresponds to one logical drive.
        /// Bit 0 represents drive A, bit 1 represents drive B, and so on.
        /// </summary>
        public uint UnitMask { get; private set; }

        /// <summary>
        /// Change affects media in drive. If not set, change affects physical device or drive.
        /// </summary>
        public bool IsMediaEvent { get; private set; }

        /// <summary>
        /// Indicated logical volume is a network volume.
        /// </summary>
        public bool IsNetworkVolume { get; private set; }
    }

    #endregion

    #region HardwareProfileEvent classes

    /// <summary>
    /// Specifies the type of <see cref="ServiceApplication.HardwareProfileEvent"/> event.
    /// </summary>
    public enum HardwareProfileEventType
    {
        /// <summary>
        /// The current configuration has changed, due to a dock or undock.
        /// </summary>
        ConfigChanged = 0x0018,

        /// <summary>
        /// Request permission to change the current configuration (dock or undock).
        /// </summary>
        QueryChangeConfig = 0x0017,

        /// <summary>
        /// Request to change the current configuration (dock or undock) has been canceled.
        /// </summary>
        ConfigChangeCanceled = 0x0019,
    }

    /// <summary>
    /// Provides data for the <see cref="ServiceApplication.HardwareProfileEvent"/> event.
    /// </summary>
    public sealed class HardwareProfileEventArgs : CancelEventArgs
    {
        internal static HardwareProfileEventArgs FromNative(int eventType, IntPtr eventData)
        {
            // check the input parameter and return the the args
            if (!Enum.IsDefined(typeof(HardwareProfileEventType), eventType))
                return null;
            return new HardwareProfileEventArgs((HardwareProfileEventType)eventType);
        }

#if DEBUG
        internal bool ToNative(out int eventType, out IntPtr eventData)
        {
            // store the type
            eventType = (int)Type;
            eventData = IntPtr.Zero;
            return false;
        }
#endif

        public HardwareProfileEventArgs(HardwareProfileEventType type)
        {
            ServiceApplication.CheckEnum("type", type);
            Type = type;
        }

        /// <summary>
        /// Gets the type of hardware profile event.
        /// </summary>
        public HardwareProfileEventType Type { get; private set; }

        public override bool CanBeCanceled
        {
            get { return Type == HardwareProfileEventType.QueryChangeConfig; }
        }
    }

    #endregion

    #region PowerEvent classes

    /// <summary>
    /// Specifies the type of <see cref="ServiceApplication.PowerEvent"/> event.
    /// </summary>
    public enum PowerEventType
    {
        /// <summary>
        /// Request for permission to suspend.
        /// </summary>
        QuerySuspend = 0x0,

        /// <summary>
        /// Suspension request denied.
        /// </summary>
        QuerySuspendFailed = 0x2,

        /// <summary>
        /// System is suspending operation.
        /// </summary>
        Suspend = 0x4,

        /// <summary>
        /// Operation resuming after critical suspension.
        /// </summary>
        ResumeCritical = 0x6,

        /// <summary>
        /// Operation is resuming from a low-power state.
        /// </summary>
        ResumeSuspend = 0x7,

        /// <summary>
        /// Battery power is low.
        /// </summary>
        BatteryLow = 0x9,

        /// <summary>
        /// Power status has changed.
        /// </summary>
        PowerStatusChange = 0x10,

        /// <summary>
        /// OEM-defined event occurred.
        /// </summary>
        OemEvent = 0xB,

        /// <summary>
        /// Operation is resuming automatically from a low-power state.
        /// This message is sent every time the system resumes.
        /// </summary>
        ResumeAutomatic = 0x12,

        /// <summary>
        /// A power setting change event has been received.
        /// </summary>
        PowerSettingChange = 0x8013,
    }

    /// <summary>
    /// Provides data for the <see cref="ServiceApplication.PowerEvent"/> event.
    /// </summary>
    public abstract class PowerEventArgs : CancelEventArgs
    {
        internal static PowerEventArgs FromNative(int eventType, IntPtr eventData)
        {
            // check if the type is known
            if (!Enum.IsDefined(typeof(PowerEventType), eventType))
                return null;
            var type = (PowerEventType)eventType;

            // return the corresponding event args
            switch (type)
            {
                case PowerEventType.OemEvent:
                    return new OemPowerEventArgs((ushort)eventData.ToInt32());
                case PowerEventType.PowerSettingChange:
                    var buffer = new byte[Marshal.ReadInt32(eventData, Marshal.SizeOf(typeof(Guid)))];
                    Marshal.Copy(new IntPtr(eventData.ToInt64() + Marshal.SizeOf(typeof(Guid)) + Marshal.SizeOf(typeof(Int32))), buffer, 0, buffer.Length);
                    return new SettingChangePowerEventArgs((Guid)Marshal.PtrToStructure(eventData, typeof(Guid)), buffer);
                default:
                    return new SimplePowerEventArgs(type);
            }
        }

#if DEBUG
        internal bool ToNative(out int eventType, out IntPtr eventData)
        {
            // store the type
            eventType = (int)Type;

            // store the Code, Data or nothing
            switch (Type)
            {
                case PowerEventType.OemEvent:
                    eventData = new IntPtr(((OemPowerEventArgs)this).Code);
                    return false;
                case PowerEventType.PowerSettingChange:
                    eventData = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Guid)) + Marshal.SizeOf(typeof(Int32)) + ((SettingChangePowerEventArgs)this).Data.Length);
                    Marshal.StructureToPtr(((SettingChangePowerEventArgs)this).Setting, eventData, false);
                    Marshal.WriteInt32(eventData, Marshal.SizeOf(typeof(Guid)), ((SettingChangePowerEventArgs)this).Data.Length);
                    Marshal.Copy(((SettingChangePowerEventArgs)this).Data, 0, new IntPtr(eventData.ToInt64() + Marshal.SizeOf(typeof(Guid)) + Marshal.SizeOf(typeof(Int32))), ((SettingChangePowerEventArgs)this).Data.Length);
                    return true;
                default:
                    eventData = IntPtr.Zero;
                    return false;
            }
        }
#endif

        internal PowerEventArgs(PowerEventType type)
        {
            ServiceApplication.CheckEnum("type", type);
            Type = type;
        }

        /// <summary>
        /// Gets the type of power event that occurred.
        /// </summary>
        public PowerEventType Type { get; private set; }

        public override bool CanBeCanceled
        {
            get { return Type == PowerEventType.QuerySuspend; }
        }
    }

    /// <summary>
    /// Provides data for events that aren't <see cref="PowerEventType.OemEvent"/> or <see cref="PowerEventType.PowerSettingChange"/> typed.
    /// </summary>
    public sealed class SimplePowerEventArgs : PowerEventArgs
    {
        public SimplePowerEventArgs(PowerEventType type)
            : base(type)
        {
            if (type == PowerEventType.OemEvent || type == PowerEventType.PowerSettingChange)
                throw new InvalidEnumArgumentException("type", (int)type, typeof(PowerEventType));
        }
    }

    /// <summary>
    /// Provides data for <see cref="PowerEventType.OemEvent"/> typed events.
    /// </summary>
    public sealed class OemPowerEventArgs : PowerEventArgs
    {
        public OemPowerEventArgs(ushort code)
            : base(PowerEventType.OemEvent)
        {
            if (code < 0x0200 || code > 0x02FF)
                throw new ArgumentOutOfRangeException("code");
            Code = code;
        }

        /// <summary>
        /// Gets the OEM-defined event code that was signaled by the system's APM BIOS.
        /// </summary>
        public ushort Code { get; private set; }
    }

    /// <summary>
    /// Provides data for <see cref="PowerEventType.PowerSettingChange"/> typed events.
    /// </summary>
    public sealed class SettingChangePowerEventArgs : PowerEventArgs
    {
        public SettingChangePowerEventArgs(Guid setting, byte[] data)
            : base(PowerEventType.PowerSettingChange)
        {
            if (setting == null)
                throw new ArgumentNullException("setting");
            if (data == null)
                throw new ArgumentNullException("data");
            Setting = setting;
            Data = data;
        }

        /// <summary>
        /// Gets the changed power setting.
        /// </summary>
        public Guid Setting { get; private set; }

        /// <summary>
        /// Gets the new value of the power setting.
        /// </summary>
        public byte[] Data { get; private set; }
    }

    #endregion

    #region SessionChanged classes

    /// <summary>
    /// Specifies the reason for a Terminal Services session change notice.
    /// </summary>
    public enum SessionChangedReason
    {
        /// <summary>
        /// The session was connected to the console terminal or RemoteFX session.
        /// </summary>
        ConsoleConnect = 0x1,

        /// <summary>
        /// The session was disconnected from the console terminal or RemoteFX session.
        /// </summary>
        ConsoleDisconnect = 0x2,

        /// <summary>
        /// The session was connected to the remote terminal.
        /// </summary>
        RemoteConnect = 0x3,

        /// <summary>
        /// The session was disconnected from the remote terminal.
        /// </summary>
        RemoteDisconnect = 0x4,

        /// <summary>
        /// A user has logged on to the session.
        /// </summary>
        SessionLogon = 0x5,

        /// <summary>
        /// A user has logged off the session.
        /// </summary>
        SessionLogoff = 0x6,

        /// <summary>
        /// The session has been locked.
        /// </summary>
        SessionLock = 0x7,

        /// <summary>
        /// The session has been unlocked.
        /// </summary>
        SessionUnlock = 0x8,

        /// <summary>
        /// The session has changed its remote controlled status.
        /// </summary>
        SessionRemoteControl = 0x9,

        /// <summary>
        /// Reserved for future use.
        /// </summary>
        SessionCreate = 0xA,

        /// <summary>
        /// Reserved for future use.
        /// </summary>
        SessionTerminate = 0xB,
    }

    /// <summary>
    /// Provides data for the <see cref="ServiceApplication.SessionChanged"/> event.
    /// </summary>
    public sealed class SessionChangedEventArgs : EventArgs
    {
        #region Win32

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WTSSESSION_NOTIFICATION
        {
            public int cbSize;
            public int dwSessionId;
        }

        #endregion

        internal static SessionChangedEventArgs FromNative(int eventType, IntPtr eventData)
        {
            // check the reason and return event args
            if (!Enum.IsDefined(typeof(SessionChangedReason), eventType))
                return null;
            return new SessionChangedEventArgs((SessionChangedReason)eventType, ((WTSSESSION_NOTIFICATION)Marshal.PtrToStructure(eventData, typeof(WTSSESSION_NOTIFICATION))).dwSessionId);
        }

#if DEBUG
        internal bool ToNative(out int eventType, out IntPtr eventData)
        {
            // store the reason and session id
            eventType = (int)Reason;
            eventData = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WTSSESSION_NOTIFICATION)));
            Marshal.StructureToPtr
            (
                new WTSSESSION_NOTIFICATION()
                {
                    cbSize = Marshal.SizeOf(typeof(WTSSESSION_NOTIFICATION)),
                    dwSessionId = SessionId,
                },
                eventData,
                false
            );
            return true;
        }
#endif

        public SessionChangedEventArgs(SessionChangedReason reason, int sessionId)
        {
            ServiceApplication.CheckEnum("reason", reason);
            Reason = reason;
            SessionId = sessionId;
        }

        /// <summary>
        /// Gets the reason for a Terminal Services session change.
        /// </summary>
        public SessionChangedReason Reason { get; private set; }

        /// <summary>
        /// Gets the identifier of the changed session.
        /// </summary>
        public int SessionId { get; private set; }
    }

    #endregion

    #region TimeChanged classes

    /// <summary>
    /// Provides data for the <see cref="ServiceApplication.TimeChanged"/> event.
    /// </summary>
    public sealed class TimeChangedEventArgs : EventArgs
    {
        #region Win32

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SERVICE_TIMECHANGE_INFO
        {
            public long liNewTime;
            public long liOldTime;
        }

        #endregion

        internal static TimeChangedEventArgs FromNative(int eventType, IntPtr eventData)
        {
            // return the event args
            var times = (SERVICE_TIMECHANGE_INFO)Marshal.PtrToStructure(eventData, typeof(SERVICE_TIMECHANGE_INFO));
            return new TimeChangedEventArgs(DateTime.FromFileTime(times.liNewTime), DateTime.FromFileTime(times.liOldTime));
        }

#if DEBUG
        internal bool ToNative(out int eventType, out IntPtr eventData)
        {
            // ignore the type and store the new and old time
            eventType = 0;
            eventData = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(SERVICE_TIMECHANGE_INFO)));
            Marshal.StructureToPtr
            (
                new SERVICE_TIMECHANGE_INFO()
                {
                    liNewTime = NewTime.ToFileTime(),
                    liOldTime = OldTime.ToFileTime(),
                },
                eventData,
                false
            );
            return true;
        }
#endif

        public TimeChangedEventArgs(DateTime newTime, DateTime oldTime)
        {
            NewTime = newTime;
            OldTime = oldTime;
        }

        /// <summary>
        /// Gets new system time.
        /// </summary>
        public DateTime NewTime { get; private set; }

        /// <summary>
        /// Gets the previous system time.
        /// </summary>
        public DateTime OldTime { get; private set; }
    }

    #endregion

    #region other event classes

    /// <summary>
    /// Provides data for a cancelable event.
    /// </summary>
    public abstract class CancelEventArgs : System.ComponentModel.CancelEventArgs
    {
        /// <summary>
        /// Indicates whether this event can be canceled.
        /// </summary>
        public abstract bool CanBeCanceled { get; }
    }

    /// <summary>
    /// Provides data for the <see cref="ServiceApplication.Continue"/>, <see cref="ServiceApplication.Pause"/>, <see cref="ServiceApplication.Preshutdown"/>, <see cref="ServiceApplication.Shutdown"/>, <see cref="ServiceApplication.Stop"/> event.
    /// </summary>
    public sealed class PendingOperationEventArgs : ServiceApplication.WaitEventArgs { }

    /// <summary>
    /// Provides data for the <see cref="ServiceApplication.Start"/> event.
    /// </summary>
    public sealed class StartEventArgs : ServiceApplication.WaitEventArgs
    {
        public StartEventArgs(string[] arguments)
        {
            if (arguments == null)
                throw new ArgumentNullException("arguments");
            Arguments = arguments;
        }

        /// <summary>
        /// Gets data passed by the start command. 
        /// </summary>
        public string[] Arguments { get; private set; }
    }

    /// <summary>
    /// Provides data for the <see cref="ServiceApplication.CustomCommand"/> event.
    /// </summary>
    public sealed class CustomCommandEventArgs : EventArgs
    {
        public CustomCommandEventArgs(int controlCode)
        {
            if (controlCode < 128 || controlCode > 255)
                throw new ArgumentOutOfRangeException("controlCode");
            ControlCode = controlCode;
        }

        /// <summary>
        /// Gets the control code sent to the service. 
        /// </summary>
        public int ControlCode { get; private set; }
    }

    /// <summary>
    /// Provides data for the <see cref="ServiceApplication.ThreadException"/> event.
    /// </summary>
    public sealed class ThreadExceptionEventArgs : System.ComponentModel.CancelEventArgs
    {
        public ThreadExceptionEventArgs(Exception exception)
        {
            if (exception == null)
                throw new ArgumentNullException("exception");
            Exception = exception;
        }

        /// <summary>
        /// Gets the exception that has occurred.
        /// </summary>
        public Exception Exception { get; private set; }
    }

    #endregion

    /// <summary>
    /// A static <see cref="System.ServiceProcess.ServiceBase"/> replacement for use in single-service applications.
    /// </summary>
    public static class ServiceApplication
    {
        #region Win32

        [UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        private delegate void LPSERVICE_MAIN_FUNCTION([In] int argc, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] string[] lpszArgv);

        [UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        private delegate int LPHANDLER_FUNCTION_EX([In] int dwControl, [In] int dwEventType, [In] IntPtr lpEventData, [In] IntPtr lpContext);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SERVICE_TABLE_ENTRY
        {
            public string lpServiceName;
            public LPSERVICE_MAIN_FUNCTION lpServiceProc;
            public SERVICE_TABLE_ENTRY(string name, LPSERVICE_MAIN_FUNCTION proc)
            {
                lpServiceName = name;
                lpServiceProc = proc;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEV_BROADCAST_DEVICEINTERFACE
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public uint dbcc_reserved;
            public Guid dbcc_classguid;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public char[] dbcc_name;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEV_BROADCAST_HANDLE
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public uint dbcc_reserved;
            public IntPtr dbch_handle;
            public IntPtr dbch_hdevnotify;
            public Guid dbch_eventguid;
            public int dbch_nameoffset;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public byte[] dbch_data;
        }

        private const int NO_ERROR = 0;
        private const int ERROR_NOT_READY = 21;
        private const int ERROR_CALL_NOT_IMPLEMENTED = 120;
        private const int ERROR_CANCELLED = 1223;

        [Flags]
        private enum DeviceNotify
        {
            WindowHandle = 0x00000000,
            ServiceHandle = 0x00000001,
            AllInterfaceClasses = 0x00000004,
        }

        private enum ServiceControl
        {
            Stop = 0x00000001,
            Pause = 0x00000002,
            Continue = 0x00000003,
            Interrogate = 0x00000004,
            Shutdown = 0x00000005,
            ParamChange = 0x00000006,
            NetBindAdd = 0x00000007,
            NetBindRemove = 0x00000008,
            NetBindEnable = 0x00000009,
            NetBindDisable = 0x0000000A,
            DeviceEvent = 0x0000000B,
            HardwareProfileChange = 0x0000000C,
            PowerEvent = 0x0000000D,
            SessionEvent = 0x0000000E,
            Preshutdown = 0x0000000F,
            TimeChange = 0x00000010,
            TriggerEvent = 0x00000020,
            UserModeReboot = 0x00000040,
        }

        [Flags]
        private enum ServiceType
        {
            KernelDriver = 0x00000001,
            FileSystemDriver = 0x00000002,
            Win32OwnProcess = 0x00000010,
            Win32ShareProcess = 0x00000020,
            InteractiveProcess = 0x00000100,
        }

        private enum ServiceState
        {
            Stopped = 0x00000001,
            StartPending = 0x00000002,
            StopPending = 0x00000003,
            Running = 0x00000004,
            ContinuePending = 0x00000005,
            PausePending = 0x00000006,
            Paused = 0x00000007,
        }

        [Flags]
        private enum AcceptedControlCode
        {
            Stop = 0x00000001,
            PauseContinue = 0x00000002,
            Shutdown = 0x00000004,
            ParamChange = 0x00000008,
            NetBindChange = 0x00000010,
            HardwareProfileChange = 0x00000020,
            PowerEvent = 0x00000040,
            SessionChange = 0x00000080,
            Preshutdown = 0x00000100,
            TimeChange = 0x00000200,
            TriggerEvent = 0x00000400,
            UserModeReboot = 0x00000800,
        }

        [Flags]
        private enum AcceptedControlCodeEx
        {
            Stop = 0x00000001,
            PauseContinue = 0x00000002,
            Shutdown = 0x00000004,
            ParamChange = 0x00000008,
            NetBindChange = 0x00000010,
            HardwareProfileChange = 0x00000020,
            PowerEvent = 0x00000040,
            SessionChange = 0x00000080,
            Preshutdown = 0x00000100,
            TimeChange = 0x00000200,
            TriggerEvent = 0x00000400,
            UserModeReboot = 0x00000800,
            Continue = 0x00010000,
            Pause = 0x00020000,
            DeviceEvent = 0x00040000,
            CustomCommand = 0x00080000,
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ServiceStatus
        {
            public ServiceType ServiceType;
            public volatile ServiceState CurrentState;
            public AcceptedControlCode ControlsAccepted;
            public volatile int Win32ExitCode;
            public int ServiceSpecificExitCode;
            public uint CheckPoint;
            public uint WaitHint;
        }

#if DEBUG
        private static readonly object debugLock = new object();
        private static readonly ManualResetEvent debugReady = new ManualResetEvent(false);
        private static IntPtr debugStatusHandle = IntPtr.Zero;
        private static LPSERVICE_MAIN_FUNCTION debugMain = null;
        private static LPHANDLER_FUNCTION_EX debugHandler;
        private static IntPtr debugContext;
        private static ServiceStatus debugOldStatus;
        private const int ERROR_PROCESS_ABORTED = 1067;
        private const int ERROR_SERVICE_NOT_IN_EXE = 1083;
        private const int ERROR_INVALID_HANDLE = 6;
        private const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
        private const int ERROR_SERVICE_START_HANG = 1070;
        private const int ERROR_INVALID_DATA = 13;
        private const int ERROR_SERVICE_NOT_ACTIVE = 1062;
        private const int DBT_DEVTYP_DEVICEINTERFACE = 5;
        private const int DBT_DEVTYP_HANDLE = 6;

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern void SetLastError([In] int dwErrCode);

        private class DebugExitException : Exception { }

        #region console handling

        private static readonly List<string> debugStringHistory = new List<string>();
        private static readonly Dictionary<Type, Array> debugEnumHistory = new Dictionary<Type, Array>();
        private static readonly bool[] debugBoolHistory = new bool[] { true, false };
        private static readonly List<Guid> debugGuidHistory = new List<Guid>() { Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        private static readonly List<DateTime> debugDateTimeHistory = new List<DateTime>() { DateTime.Now, DateTime.Today, DateTime.UtcNow };
        private static readonly List<IntPtr> debugPointerHistory = new List<IntPtr>() { IntPtr.Zero };
        private static readonly List<int> debugIntegerHistory = new List<int>() { int.MinValue, int.MaxValue };
        private static readonly List<ushort> debugUnsignedShortHistory = new List<ushort>() { ushort.MinValue, ushort.MaxValue };
        private static readonly List<uint> debugUnsignedIntegerHistory = new List<uint>() { uint.MinValue, uint.MaxValue };
        private static readonly List<byte[]> debugBinaryHistory = new List<byte[]>();
        private static readonly System.Text.StringBuilder debugQueryInput = new System.Text.StringBuilder();
        private static string debugQueryText = null;
        private static int debugQueryPosition;

        private delegate bool DebugQueryParser<T>(string input, out T result);

        private static void DebugWriteQueryWithinLock()
        {
            // calculate the current scroll window (and ensure it's at least one plus two scrollers)
            var scrollWindow = Console.BufferWidth - debugQueryText.Length;
            if (scrollWindow < 3)
                Console.BufferWidth = debugQueryText.Length + (scrollWindow = 3);

            // write the query text
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(debugQueryText);

            // write the input
            Console.ForegroundColor = ConsoleColor.Gray;
            if (debugQueryInput.Length < scrollWindow)
            {
                // everything fits
                Console.Write(debugQueryInput);
                Console.CursorLeft = debugQueryText.Length + debugQueryPosition;
            }
            else
            {
                if (debugQueryInput.Length == scrollWindow)
                {
                    // only scroll if the cursor is outside the scroll window (which results in a left scroller)
                    if (debugQueryPosition < scrollWindow)
                    {
                        Console.Write(debugQueryInput);
                        Console.CursorTop--;
                        Console.CursorLeft = debugQueryText.Length + debugQueryPosition;
                    }
                    else
                    {
                        Console.Write('◄');
                        Console.Write(debugQueryInput.ToString(2, scrollWindow - 2));
                    }
                }
                else if (debugQueryInput.Length - debugQueryPosition < scrollWindow)
                {
                    // align to the right if the end and cursor fit in the scroll window (minus the left scroller)
                    Console.Write('◄');
                    if (debugQueryPosition < debugQueryInput.Length)
                    {
                        Console.Write(debugQueryInput.ToString(debugQueryInput.Length - scrollWindow + 1, scrollWindow - 1));
                        Console.CursorTop--;
                        Console.CursorLeft = debugQueryText.Length + scrollWindow - (debugQueryInput.Length - debugQueryPosition);
                    }
                    else
                        Console.Write(debugQueryInput.ToString(debugQueryInput.Length - scrollWindow + 2, scrollWindow - 2));
                }
                else if (debugQueryPosition < (scrollWindow - 1))
                {
                    // align to the left if the start and cursor fit into the scroll window (minus the right scroller)
                    Console.Write(debugQueryInput.ToString(0, scrollWindow - 1));
                    Console.Write('►');
                    Console.CursorTop--;
                    Console.CursorLeft = debugQueryText.Length + debugQueryPosition;
                }
                else
                {
                    // nothing fits nicely, just move the cursor to the right-most position in the scroll window (minus both scrollers)
                    Console.Write('◄');
                    Console.Write(debugQueryInput.ToString(debugQueryPosition - scrollWindow + 3, scrollWindow - 2));
                    Console.Write('►');
                    Console.CursorTop--;
                    Console.CursorLeft = debugQueryText.Length + scrollWindow - 2;
                }
            }
        }

        private static void DebugRemoveQueryWithinLock()
        {
            // remove the previous line
            Console.CursorLeft = 0;
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(new string(' ', Console.BufferWidth));
            Console.CursorTop--;
        }

        private static void DebugWriteLine(string what, ConsoleColor color)
        {
            // write something in pretty colors
            lock (debugLock)
            {
                if (debugQueryText != null)
                    DebugRemoveQueryWithinLock();
                Console.ForegroundColor = color;
                Console.WriteLine(what);
                if (debugQueryText != null)
                    DebugWriteQueryWithinLock();
            }
        }

        private static string DebugQueryInternal(string what, bool allowNull, string[] choices)
        {
            // query the user
            var msg = new ArgumentNullException().Message;
            while (true)
            {
                // start the read process
                lock (debugLock)
                {
                    if (debugQueryText != null)
                        throw new InvalidOperationException();
                    debugQueryText = what + ": ";
                    debugQueryPosition = 0;
                    debugQueryInput.Length = 0;
                    DebugWriteQueryWithinLock();
                }

                // prepare to read the input
                if (choices != null && choices.Length == 0)
                    choices = null;
                var choicePosition = -1;
                var tabChoices = (string[])null;
                var tabChoicePosition = 0;
                while (true)
                {
                    // handle the next console event
                    var key = Console.ReadKey(true);
                    lock (debugLock)
                    {
                        switch (key.Key)
                        {
                            // remove char
                            case ConsoleKey.Backspace: if (debugQueryPosition > 0) { debugQueryInput.Remove(--debugQueryPosition, 1); tabChoices = null; } break;
                            case ConsoleKey.Delete: if (debugQueryPosition < debugQueryInput.Length) { debugQueryInput.Remove(debugQueryPosition, 1); tabChoices = null; } break;
                            case ConsoleKey.Escape: debugQueryPosition = 0; debugQueryInput.Length = 0; tabChoices = null; break;

                            // insert char
                            default:
                                if (!char.IsControl(key.KeyChar))
                                {
                                    debugQueryInput.Insert(debugQueryPosition++, key.KeyChar);
                                    tabChoices = null;
                                }
                                break;

                            // quit on control-c
                            case ConsoleKey.C:
                                if (key.Modifiers == ConsoleModifiers.Control)
                                    throw new DebugExitException();
                                goto default;

                            // move the cursor
                            case ConsoleKey.Home: debugQueryPosition = 0; break;
                            case ConsoleKey.End: debugQueryPosition = debugQueryInput.Length; break;
                            case ConsoleKey.LeftArrow: if (debugQueryPosition > 0) debugQueryPosition--; break;
                            case ConsoleKey.RightArrow: if (debugQueryPosition < debugQueryInput.Length) debugQueryPosition++; break;

                            // navigate through choices
                            case ConsoleKey.PageUp:
                                if (choices == null)
                                    break;
                                choicePosition = choices.Length - 1;
                                goto case ConsoleKey.F5;
                            case ConsoleKey.UpArrow:
                                if (choices == null || choicePosition == choices.Length - 1)
                                    break;
                                choicePosition++;
                                goto case ConsoleKey.F5;
                            case ConsoleKey.DownArrow:
                                if (choices == null || choicePosition < 1)
                                    break;
                                choicePosition--;
                                goto case ConsoleKey.F5;
                            case ConsoleKey.PageDown:
                                if (choices == null)
                                    break;
                                choicePosition = 0;
                                goto case ConsoleKey.F5;
                            case ConsoleKey.F5:
                                if (choices == null || choicePosition == -1)
                                    break;
                                debugQueryInput.Length = 0;
                                debugQueryInput.Append(choices[choicePosition]);
                                debugQueryPosition = debugQueryInput.Length;
                                tabChoices = null;
                                break;

                            // provide auto-completion
                            case ConsoleKey.Tab:
                                if (tabChoices == null)
                                {
                                    // try to find tab choices
                                    if (choices == null)
                                        break;
                                    var input = debugQueryInput.ToString().Trim();
                                    tabChoices = Array.FindAll(choices, choice => choice.StartsWith(input, StringComparison.InvariantCultureIgnoreCase));
                                    if (tabChoices.Length == 0)
                                    {
                                        tabChoices = null;
                                        break;
                                    }
                                    tabChoicePosition = 0;
                                }
                                else
                                {
                                    // navigate through the tab choices
                                    if ((key.Modifiers & ConsoleModifiers.Shift) == 0)
                                    {
                                        tabChoicePosition++;
                                        if (tabChoicePosition >= tabChoices.Length)
                                            tabChoicePosition = 0;
                                    }
                                    else
                                    {
                                        tabChoicePosition--;
                                        if (tabChoicePosition < 0)
                                            tabChoicePosition = tabChoices.Length - 1;
                                    }
                                }
                                debugQueryInput.Length = 0;
                                debugQueryInput.Append(tabChoices[tabChoicePosition]);
                                debugQueryPosition = debugQueryInput.Length;
                                break;
                        }

                        // quit the loop on enter
                        if (key.Key == ConsoleKey.Enter)
                        {
                            Console.WriteLine();
                            break;
                        }

                        // correct for resized buffers and rewrite the query
                        if (Console.CursorLeft == 0)
                            Console.CursorTop--;
                        DebugRemoveQueryWithinLock();
                        DebugWriteQueryWithinLock();
                    }
                }

                // end the read process
                lock (debugLock)
                {
                    debugQueryText = null;
                    var result = debugQueryInput.ToString().Trim();
                    if (result.Length > 0)
                        return result;
                    if (allowNull)
                        return null;
                    DebugWriteLine(msg, ConsoleColor.Magenta);
                }
            }
        }

        private static T DebugQueryInternal<T>(string what, DebugQueryParser<T> parser, IList<T> history, bool allowNull = false, Converter<T, string> formatter = null)
        {
            // prepare the message for invalid input
            var msg = new ArgumentException().Message;
            T result = default(T);

            // query the user until we get a valid result
            string s;
            while ((s = DebugQueryInternal(what, allowNull, Array.ConvertAll(history.ToArray(), formatter ?? (t => t.ToString())))) != null && !parser(s, out result))
                DebugWriteLine(msg, ConsoleColor.Magenta);

            // adjust the history if possible
            if (s != null && !((System.Collections.IList)history).IsReadOnly)
            {
                var index = history.IndexOf(result);
                switch (index)
                {
                    case 0:
                        break;
                    case -1:
                        if (!((System.Collections.IList)history).IsFixedSize)
                            history.Insert(0, result);
                        break;
                    default:
                        for (var i = index; i > 0; i--)
                            history[i] = history[i - 1];
                        history[0] = result;
                        break;
                }
            }

            // return the result
            return result;
        }

        private static T DebugQueryInternal<T>(string what, DebugQueryParser<T> parser, IList<T> history, T min, T max, Converter<T, string> formatter = null) where T : struct, IComparable<T>
        {
            // repeatedly query the user until we get something within bounds
            var msg = new ArgumentOutOfRangeException().Message;
            while (true)
            {
                var result = DebugQueryInternal<T>(what, parser, history, false, formatter);
                if (min.CompareTo(result) <= 0 && max.CompareTo(result) >= 0)
                    return result;
                DebugWriteLine(msg, ConsoleColor.Magenta);
            }
        }

        private static string DebugQueryString(string name, bool allowNull = false)
        {
            return DebugQueryInternal<string>
            (
                name,
                (string input, out string result) =>
                {
                    // allow the use of two double quotes as emtpy string
                    result = input == "\"\"" ? string.Empty : input;
                    return true;
                },
                debugStringHistory,
                allowNull
            );
        }

        private static DateTime DebugQueryDateTime(string name)
        {
            return DebugQueryInternal<DateTime>(name, DateTime.TryParse, debugDateTimeHistory);
        }

        private static int DebugQueryInteger(string name, int min = int.MinValue, int max = int.MaxValue)
        {
            return DebugQueryInternal<int>(name, int.TryParse, debugIntegerHistory, min, max);
        }

        private static uint DebugQueryUnsignedInteger(string name, uint min = uint.MinValue, uint max = uint.MaxValue)
        {
            return DebugQueryInternal<uint>(name, uint.TryParse, debugUnsignedIntegerHistory, min, max);
        }

        private static ushort DebugQueryUnsignedShort(string name, ushort min = ushort.MinValue, ushort max = ushort.MaxValue)
        {
            return DebugQueryInternal<ushort>(name, ushort.TryParse, debugUnsignedShortHistory, min, max);
        }

        private static Guid DebugQueryGuid(string name)
        {
            return DebugQueryInternal<Guid>
            (
                name,
                (string input, out Guid result) =>
                {
                    if (input != null)
                    {
                        try
                        {
                            result = new Guid(input);
                            return true;
                        }
                        catch (FormatException) { }
                        catch (OverflowException) { }
                    }
                    result = Guid.Empty;
                    return false;
                },
                debugGuidHistory
            );
        }

        private static bool DebugQueryBool(string name)
        {
            return DebugQueryInternal<bool>(name, bool.TryParse, debugBoolHistory);
        }

        private static byte[] DebugQueryBinary(string name, bool allowNull = false)
        {
            return DebugQueryInternal
            (
                name,
                (string input, out byte[] result) =>
                {
                    // make a crude check
                    if (input == null)
                        goto InvalidArray;

                    // check if the input is string that should be converted to binary
                    input = input.Trim();
                    if (input.EndsWith("\""))
                    {
                        if (input.StartsWith("L\""))
                            result = System.Text.Encoding.Unicode.GetBytes(input);
                        else if (input.StartsWith("\""))
                            result = System.Text.Encoding.Default.GetBytes(input);
                        else
                            goto InvalidArray;
                        return true;
                    }

                    // remove the leading hex specifier
                    if (input.StartsWith("0x"))
                        input = input.Substring(2);

                    // enumerate over every char
                    var lastNumber = -1;
                    var list = new List<byte>(input.Length / 2);
                    foreach (char ch in input)
                    {
                        // skip spaces
                        if (ch == ' ' || ch == '\t')
                            continue;

                        // get and check the digit
                        int currentNumber;
                        if (ch >= '0' && ch <= '9')
                            currentNumber = ch - '0';
                        else if (ch >= 'A' && ch <= 'F')
                            currentNumber = ch - 'A';
                        else if (ch >= 'a' && ch <= 'f')
                            currentNumber = ch - 'a';
                        else
                            goto InvalidArray;

                        // either append the two digits as byte or remember the current digit
                        if (lastNumber != -1)
                        {
                            list.Add((byte)(lastNumber << 4 | currentNumber));
                            lastNumber = -1;
                        }
                        else
                            lastNumber = currentNumber;
                    }

                    // if everything was read properly store the array and return success
                    if (lastNumber == -1)
                    {
                        result = list.ToArray();
                        return true;
                    }

                InvalidArray:
                    // in case something went wrong 
                    result = null;
                    return false;
                },
                debugBinaryHistory,
                allowNull,
                binary => binary.Aggregate("0x", (s, b) => s + b.ToString("X2"))
            );
        }

        private static IntPtr DebugQueryPointer(string name, bool allowZero = false)
        {
            // query the user for a pointer that may not be null
            var msg = new ArgumentNullException().Message;
            IntPtr checkedResult;
            while
            (
                IntPtr.Zero ==
                (
                    checkedResult = DebugQueryInternal<IntPtr>
                    (
                        name,
                        (string input, out IntPtr result) =>
                        {
                            // check the input
                            if (input != null)
                            {
                                // remove the leading hex specifier
                                input = input.TrimStart();
                                if (input.StartsWith("0x"))
                                    input = input.Substring(2);

                                // parse the input as hex number
                                long val;
                                if (long.TryParse(input, System.Globalization.NumberStyles.HexNumber, null, out val))
                                {
                                    // convert and return the number
                                    result = new IntPtr(val);
                                    return true;
                                }
                            }

                            // in case something went wrong 
                            result = IntPtr.Zero;
                            return false;
                        },
                        debugPointerHistory,
                        false,
                        p => "0x" + p.ToString("X" + IntPtr.Size)
                    )
                ) &&
                !allowZero
            )
                DebugWriteLine(msg, ConsoleColor.Magenta);
            return checkedResult;
        }

        private static T DebugQueryEnum<T>(string name, bool allowAnyValue = false) where T : struct
        {
            // retrieve the cached history
            Array values;
            if (!debugEnumHistory.TryGetValue(typeof(T), out values))
                debugEnumHistory.Add(typeof(T), values = Enum.GetValues(typeof(T)));

            // query the enum value
            return DebugQueryInternal<T>
            (
                name,
                (string s, out T t) =>
                {
                    // try to parse the string
                    if (s != null)
                    {
                        try
                        {
                            t = (T)Enum.Parse(typeof(T), s, true);
                            return true;
                        }
                        catch (ArgumentException)
                        {
                            // if any value is allowed, try to parse the integer
                            if (allowAnyValue)
                            {
                                int i;
                                if (int.TryParse(s, out i))
                                {
                                    t = (T)Enum.ToObject(typeof(T), i);
                                    return true;
                                }
                            };
                        }
                    }

                    // invalid input
                    t = default(T);
                    return false;
                },
                (T[])values
            );
        }

        #endregion

        #region registration handling

        private static readonly Dictionary<IntPtr, KeyValuePair<DebugNotificationType, object>> debugNotifications = new Dictionary<IntPtr, KeyValuePair<DebugNotificationType, object>>();
        private static int debugNextNotificationHandle = 1;

        private enum DebugNotificationType
        {
            Device,
            PowerSetting,
        }

        private static IntPtr DebugRegisterNotification(bool invalid, IntPtr recipient, DeviceNotify flags, DebugNotificationType type, object o)
        {
            // check the input parameters
            if (invalid || ((flags & DeviceNotify.WindowHandle) != 0 && (flags & DeviceNotify.ServiceHandle) != 0))
            {
                SetLastError(ERROR_INVALID_DATA);
                return IntPtr.Zero;
            }
            if ((flags & DeviceNotify.ServiceHandle) == 0 || recipient == IntPtr.Zero || recipient != debugStatusHandle)
            {
                SetLastError(ERROR_INVALID_HANDLE);
                return IntPtr.Zero;
            }

            // add the notification to the list
            lock (debugNotifications)
            {
                var handle = new IntPtr(debugNextNotificationHandle++);
                debugNotifications.Add(handle, new KeyValuePair<DebugNotificationType, object>(type, o));
                return handle;
            }
        }

        private static bool DebugUnregisterNotification(IntPtr handle, DebugNotificationType type)
        {
            // remove the notification if the handle exists
            KeyValuePair<DebugNotificationType, object> entry;
            if (handle != IntPtr.Zero)
                lock (debugNotifications)
                    if (debugNotifications.TryGetValue(handle, out entry) && entry.Key == type)
                        return debugNotifications.Remove(handle);
            SetLastError(ERROR_INVALID_HANDLE);
            return false;
        }

        #endregion

        private static IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, DeviceNotify Flags)
        {
            return DebugRegisterNotification
            (
                PowerSettingGuid == Guid.Empty,
                hRecipient,
                Flags,
                DebugNotificationType.PowerSetting,
                PowerSettingGuid
            );
        }

        private static bool UnregisterPowerSettingNotification(IntPtr Handle)
        {
            return DebugUnregisterNotification(Handle, DebugNotificationType.PowerSetting);
        }

        private static IntPtr RegisterDeviceNotification(IntPtr hRecipient, ref DEV_BROADCAST_HANDLE NotificationFilter, DeviceNotify Flags)
        {
            return DebugRegisterNotification
            (
                (
                    (Flags & DeviceNotify.AllInterfaceClasses) != 0 ||
                    NotificationFilter.dbcc_size != Marshal.SizeOf(typeof(DEV_BROADCAST_HANDLE)) ||
                    NotificationFilter.dbcc_devicetype != DBT_DEVTYP_HANDLE ||
                    NotificationFilter.dbch_handle == IntPtr.Zero
                ),
                hRecipient,
                Flags,
                DebugNotificationType.Device,
                NotificationFilter.dbch_handle
            );
        }

        private static IntPtr RegisterDeviceNotification(IntPtr hRecipient, ref DEV_BROADCAST_DEVICEINTERFACE NotificationFilter, DeviceNotify Flags)
        {
            return DebugRegisterNotification
            (
                (
                    NotificationFilter.dbcc_size != Marshal.SizeOf(typeof(DEV_BROADCAST_DEVICEINTERFACE)) ||
                    NotificationFilter.dbcc_devicetype != DBT_DEVTYP_DEVICEINTERFACE ||
                    ((Flags & DeviceNotify.AllInterfaceClasses) == 0 && NotificationFilter.dbcc_classguid == Guid.Empty)
                ),
                hRecipient,
                Flags,
                DebugNotificationType.Device,
                (Flags & DeviceNotify.AllInterfaceClasses) == 0 ? NotificationFilter.dbcc_classguid : Guid.Empty
            );
        }

        private static bool UnregisterDeviceNotification(IntPtr Handle)
        {
            return DebugUnregisterNotification(Handle, DebugNotificationType.Device);
        }

        private static bool SetServiceStatus(IntPtr hServiceStatus, ref ServiceStatus lpServiceStatus)
        {
            // check the input data
            if (hServiceStatus == IntPtr.Zero || hServiceStatus != debugStatusHandle)
            {
                SetLastError(ERROR_INVALID_HANDLE);
                return false;
            }
            if
            (
                (lpServiceStatus.ServiceType & ServiceType.Win32OwnProcess) == 0 ||
                (lpServiceStatus.ServiceType & ServiceType.Win32ShareProcess) != 0 ||
                !Enum.IsDefined(typeof(ServiceState), lpServiceStatus.CurrentState)
            )
            {
                SetLastError(ERROR_INVALID_DATA);
                return false;
            }

            // make sure that once the service enters stopped nothing else is allowed and copy the old status
            ServiceStatus oldStatus;
            lock (debugLock)
            {
                if (debugOldStatus.CurrentState == ServiceState.Stopped)
                {
                    SetLastError(ERROR_SERVICE_NOT_ACTIVE);
                    return false;
                }
                oldStatus = debugOldStatus;
                debugOldStatus = lpServiceStatus;
            }

            // log the difference between the old and new status
            if (oldStatus.CurrentState != lpServiceStatus.CurrentState)
                LogEvent(EventLogEntryType.Information, "Status: {0}", lpServiceStatus.CurrentState);
            if (oldStatus.ControlsAccepted != lpServiceStatus.ControlsAccepted)
                LogEvent(EventLogEntryType.Information, "Accepted Controls: {0}", lpServiceStatus.ControlsAccepted);
            if (oldStatus.Win32ExitCode != lpServiceStatus.Win32ExitCode)
                LogEvent(EventLogEntryType.Warning, "Exit Code: {0}", lpServiceStatus.Win32ExitCode);
            return true;
        }

        private static IntPtr RegisterServiceCtrlHandlerEx(string lpServiceName, LPHANDLER_FUNCTION_EX lpHandlerProc, IntPtr lpContext)
        {
            // check the input data
            if (lpHandlerProc == null)
            {
                SetLastError(ERROR_INVALID_DATA);
                return IntPtr.Zero;
            }

            // complete work
            lock (debugLock)
            {
                // ensure that StartServiceCtrlDispatcher has been called
                if (debugMain == null)
                {
                    SetLastError(ERROR_SERVICE_NOT_IN_EXE);
                    return IntPtr.Zero;
                }

                // ensure that this method hasn't already been called
                if (debugStatusHandle != IntPtr.Zero)
                {
                    SetLastError(ERROR_SERVICE_ALREADY_RUNNING);
                    return IntPtr.Zero;
                }

                // generate the handle
                debugStatusHandle = new IntPtr(new Random().Next(int.MaxValue - 1) + 1);
                debugContext = lpContext;
                debugHandler = lpHandlerProc;
                debugReady.Set();
            }

            // return the handle
            return debugStatusHandle;
        }

        private static bool StartServiceCtrlDispatcher(SERVICE_TABLE_ENTRY[] lpServiceTable)
        {
            // check the input data
            if
            (
                lpServiceTable == null ||
                lpServiceTable.Length != 2 ||
                lpServiceTable[1].lpServiceName != null ||
                lpServiceTable[1].lpServiceProc != null ||
                lpServiceTable[0].lpServiceName == null ||
                lpServiceTable[0].lpServiceProc == null
            )
            {
                SetLastError(ERROR_INVALID_DATA);
                return false;
            }

            // ensure that we haven't been started before
            lock (debugLock)
            {
                if (debugMain != null)
                {
                    SetLastError(ERROR_SERVICE_ALREADY_RUNNING);
                    return false;
                }
                debugMain = lpServiceTable[0].lpServiceProc;
            }

            // start the main thread
            var mainThread = new Thread(arg => debugMain(arg == null ? 0 : ((string[])arg).Length, (string[])arg));
            var args = Environment.GetCommandLineArgs();
            mainThread.Start(args.Length > 1 ? args.Skip(1).ToArray() : null);
            if (!debugReady.WaitOne(30000))
                Environment.Exit(ERROR_SERVICE_START_HANG);

            // enter the dispatching loop
            try
            {
                while (true)
                {
                    // get the next control
                    var control = DebugQueryEnum<ServiceControl>("Control", true);

                    // set the eventType and eventData
                    int eventType;
                    IntPtr eventData;
                    bool cleanUp;
                    switch (control)
                    {
                        // get the native device event
                        case ServiceControl.DeviceEvent:
                            switch (DebugQueryEnum<DeviceEventDeviceType>("Device Type"))
                            {
                                case DeviceEventDeviceType.Interface:
                                    cleanUp = new InterfaceDeviceEventArgs
                                    (
                                        DebugQueryEnum<DeviceEventType>("Event Type"),
                                        DebugQueryGuid("Class"),
                                        DebugQueryString("Name", true) ?? string.Empty
                                    ).ToNative(out eventType, out eventData);
                                    break;
                                case DeviceEventDeviceType.Handle:
                                    cleanUp = new HandleDeviceEventArgs
                                    (
                                        DebugQueryEnum<DeviceEventType>("Event Type"),
                                        DebugQueryPointer("Handle", false),
                                        DebugQueryGuid("Event Guid"),
                                        DebugQueryString("Name", true),
                                        DebugQueryBinary("Data", true)
                                    ).ToNative(out eventType, out eventData);
                                    break;
                                case DeviceEventDeviceType.Oem:
                                    cleanUp = new OemDeviceEventArgs
                                    (
                                        DebugQueryEnum<DeviceEventType>("Event Type"),
                                        DebugQueryUnsignedInteger("Identifier"),
                                        DebugQueryUnsignedInteger("Supplied Function")
                                    ).ToNative(out eventType, out eventData);
                                    break;
                                case DeviceEventDeviceType.Port:
                                    cleanUp = new PortDeviceEventArgs
                                    (
                                        DebugQueryEnum<DeviceEventType>("Event Type"),
                                        DebugQueryString("Name")
                                    ).ToNative(out eventType, out eventData);
                                    break;
                                case DeviceEventDeviceType.Volume:
                                    cleanUp = new VolumeDeviceEventArgs
                                    (
                                        DebugQueryEnum<DeviceEventType>("Event Type"),
                                        DebugQueryUnsignedInteger("Unit Mask"),
                                        DebugQueryBool("Is Media Event"),
                                        DebugQueryBool("Is Network Event")
                                    ).ToNative(out eventType, out eventData);
                                    break;
                                default:
                                    cleanUp = new UnknownDeviceEventArgs(DebugQueryEnum<DeviceEventType>("Event Type")).ToNative(out eventType, out eventData);
                                    break;
                            }
                            break;

                        // get the native hardware profile event
                        case ServiceControl.HardwareProfileChange:
                            cleanUp = new HardwareProfileEventArgs(DebugQueryEnum<HardwareProfileEventType>("Type")).ToNative(out eventType, out eventData);
                            break;

                        // get the native power event
                        case ServiceControl.PowerEvent:
                            var powerEventType = DebugQueryEnum<PowerEventType>("Type");
                            switch (powerEventType)
                            {
                                case PowerEventType.OemEvent:
                                    cleanUp = new OemPowerEventArgs(DebugQueryUnsignedShort("Code", 0x0200, 0x20FF)).ToNative(out eventType, out eventData);
                                    break;
                                case PowerEventType.PowerSettingChange:
                                    cleanUp = new SettingChangePowerEventArgs(DebugQueryGuid("Setting"), DebugQueryBinary("Data")).ToNative(out eventType, out eventData);
                                    break;
                                default:
                                    cleanUp = new SimplePowerEventArgs(powerEventType).ToNative(out eventType, out eventData);
                                    break;
                            }
                            break;

                        // get the native session event
                        case ServiceControl.SessionEvent:
                            cleanUp = new SessionChangedEventArgs(DebugQueryEnum<SessionChangedReason>("Reason"), DebugQueryInteger("Session Id")).ToNative(out eventType, out eventData);
                            break;

                        // get the native time change event
                        case ServiceControl.TimeChange:
                            cleanUp = new TimeChangedEventArgs(DebugQueryDateTime("New Time"), DebugQueryDateTime("Old Time")).ToNative(out eventType, out eventData);
                            break;

                        // control codes that don't require any additional params
                        case ServiceControl.Interrogate:
                        case ServiceControl.NetBindAdd:
                        case ServiceControl.NetBindDisable:
                        case ServiceControl.NetBindEnable:
                        case ServiceControl.NetBindRemove:
                        case ServiceControl.Continue:
                        case ServiceControl.ParamChange:
                        case ServiceControl.Pause:
                        case ServiceControl.Preshutdown:
                        case ServiceControl.Shutdown:
                        case ServiceControl.Stop:
                        case ServiceControl.TriggerEvent:
                        case ServiceControl.UserModeReboot:
                        default:
                            eventType = 0;
                            eventData = IntPtr.Zero;
                            cleanUp = false;
                            break;
                    }

                    // call the handler and cleanup the event data afterwards if there is any
                    var result = debugHandler((int)control, eventType, eventData, debugContext);
                    LogEvent(result == NO_ERROR ? EventLogEntryType.Information : result == ERROR_CANCELLED || result == ERROR_CALL_NOT_IMPLEMENTED ? EventLogEntryType.Warning : EventLogEntryType.Error, "Result: {0}", new Win32Exception(result).Message);
                    if (cleanUp)
                        Marshal.FreeHGlobal(eventData);
                }
            }
            catch (DebugExitException)
            {
                // if the service has exited, return true
                lock (debugLock)
                    if (debugOldStatus.CurrentState == ServiceState.Stopped)
                        return true;

                // otherwise abort the process
                Environment.Exit(ERROR_PROCESS_ABORTED);
                return false;
            }
        }
#else
        private static EventLog log;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification([In] IntPtr hRecipient, [In] ref Guid PowerSettingGuid, [In] DeviceNotify Flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool UnregisterPowerSettingNotification([In] IntPtr Handle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr RegisterDeviceNotification([In] IntPtr hRecipient, [In] ref DEV_BROADCAST_HANDLE NotificationFilter, [In] DeviceNotify Flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr RegisterDeviceNotification([In] IntPtr hRecipient, [In] ref DEV_BROADCAST_DEVICEINTERFACE NotificationFilter, [In] DeviceNotify Flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool UnregisterDeviceNotification([In] IntPtr Handle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetServiceStatus([In] IntPtr hServiceStatus, [In] ref ServiceStatus lpServiceStatus);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr RegisterServiceCtrlHandlerEx([In] string lpServiceName, [In] LPHANDLER_FUNCTION_EX lpHandlerProc, [In, Optional] IntPtr lpContext);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool StartServiceCtrlDispatcher([In] SERVICE_TABLE_ENTRY[] lpServiceTable);
#endif

        #endregion

        private static readonly object initializeLock = new object();
        private static readonly object serviceLock = new object();
        private static readonly AutoResetEvent serviceMainSignal = new AutoResetEvent(false);
        private static readonly NotificationCounter<Guid> powerSettingNotifications = new NotificationCounter<Guid>();
        private static readonly NotificationCounter<IntPtr> deviceHandleNotifications = new NotificationCounter<IntPtr>();
        private static ServiceStatus status = new ServiceStatus();
        private static bool initialized = false;
        private static bool exitPrematurely = false;
        private static volatile bool quitServiceMain = false;
        private static IntPtr statusHandle = IntPtr.Zero;
        private static WaitEventArgs currentWaitEventArgs = null;
        private static EventHandler<PendingOperationEventArgs> pendingEventHandler = null;
        private static string mainServiceName = null;
        private static IntPtr deviceNotificationHandle = IntPtr.Zero;
        private static EventHandler<PendingOperationEventArgs> continueHandler;
        private static EventHandler<NetworkBindingChangedEventArgs> networkBindingChangedHandler;
        private static EventHandler<EventArgs> parametersChangedHandler;
        private static EventHandler<PendingOperationEventArgs> pauseHandler;
        private static EventHandler<PendingOperationEventArgs> preshutdownHandler;
        private static EventHandler<PendingOperationEventArgs> shutdownHandler;
        private static EventHandler<PendingOperationEventArgs> stopHandler;
        private static EventHandler<DeviceEventArgs> deviceEventHandler;
        private static EventHandler<HardwareProfileEventArgs> hardwareProfileEventHandler;
        private static EventHandler<PowerEventArgs> powerEventHandler;
        private static EventHandler<SessionChangedEventArgs> sessionChangedHandler;
        private static EventHandler<TimeChangedEventArgs> timeChangedHandler;
        private static EventHandler<EventArgs> triggerEventHandler;
        private static EventHandler<EventArgs> userModeRebootHandler;
        private static EventHandler<CustomCommandEventArgs> customCommandHandler;
        private static EventHandler<StartEventArgs> startHandler;

        [EditorBrowsable(EditorBrowsableState.Never)]
        public abstract class WaitEventArgs : EventArgs
        {
            internal WaitEventArgs() { }

            /// <summary>
            /// Requests additional time for a pending operation.
            /// </summary>
            /// <param name="requiredTime">The requested time.</param>
            /// <exception cref="InvalidOperationException">The method is called after the event has been handled.</exception>
            /// <exception cref="ArgumentOutOfRangeException"><paramref name="requiredTime"/> is not positive.</exception>
            public void RequestAdditionalTime(TimeSpan requiredTime)
            {
                // check the input param
                if (requiredTime <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException("requiredTime");

                // ensure that this is the current pending action and update the check point
                lock (serviceLock)
                {
                    if (currentWaitEventArgs != this)
                        throw new InvalidOperationException();
                    status.CheckPoint++;
                    status.WaitHint = (uint)requiredTime.TotalMilliseconds;
                    if (!SetServiceStatus(statusHandle, ref status))
                        throw new Win32Exception();
                }
            }
        }

        private class NotificationCounter<T>
        {
            private class HandleRef
            {
                private uint refCount = 1;

                public HandleRef(IntPtr handle) { Handle = handle; }

                public IntPtr Handle { get; private set; }

                public void AddRef() { if (refCount == 0) throw new InvalidOperationException(); refCount++; }

                public bool Release() { if (refCount == 0) throw new InvalidOperationException(); return --refCount == 0; }
            }

            public delegate IntPtr RegisterFunction<U>(IntPtr recipient, ref U filter, DeviceNotify flags);
            public delegate bool UnregisterFunction(IntPtr notificationHandle);

            private readonly Dictionary<T, HandleRef> handles = new Dictionary<T, HandleRef>();

            public void Register<U>(T filter, Converter<T, U> converter, RegisterFunction<U> register, Version requiredWindowsVersion)
            {
                if (Environment.OSVersion.Version < requiredWindowsVersion)
                    throw new PlatformNotSupportedException();
                Register(filter, converter, register);
            }

            public void Register<U>(T filter, Converter<T, U> converter, RegisterFunction<U> register)
            {
                // make sure the service is initialized
                if (statusHandle == IntPtr.Zero)
                    throw new InvalidOperationException();

                // create the handle reference or increase the reference counter
                lock (handles)
                {
                    HandleRef handleRef;
                    if (handles.TryGetValue(filter, out handleRef))
                    {
                        handleRef.AddRef();
                        return;
                    }
                    var convertedFilter = converter(filter);
                    var handle = register(statusHandle, ref convertedFilter, DeviceNotify.ServiceHandle);
                    if (handle == IntPtr.Zero)
                        throw new Win32Exception();
                    handles.Add(filter, new HandleRef(handle));
                }
            }

            public bool Unregister(T filter, UnregisterFunction unregister)
            {
                // release the notification handle or decrease the reference counter
                lock (handles)
                {
                    HandleRef handleRef;
                    if (!handles.TryGetValue(filter, out handleRef) || !handleRef.Release())
                        return false;
                    handles.Remove(filter);
                    return unregister(handleRef.Handle);
                }
            }
        }

        internal static void CheckEnum<T>(string paramName, T value)
        {
            // ensure that the enum value is defined
            if (!Enum.IsDefined(typeof(T), value))
                throw new InvalidEnumArgumentException(paramName, Convert.ToInt32(value), typeof(T));
        }

        private static void SetAcceptedControlsAndReportStatusWithinLock()
        {
            // generate the bitmask
            status.ControlsAccepted = 0;
            switch (status.CurrentState)
            {
                // always allow the continue command when paused in addition to everything else
                case ServiceState.Paused:
                    status.ControlsAccepted |= AcceptedControlCode.PauseContinue;
                    goto case ServiceState.Running;

                // allow state changing commands in addition to other commands
                case ServiceState.Running:
                    if (stopHandler != null) status.ControlsAccepted |= AcceptedControlCode.Stop;
                    if (pauseHandler != null || continueHandler != null) status.ControlsAccepted |= AcceptedControlCode.PauseContinue;
                    if (shutdownHandler != null) status.ControlsAccepted |= AcceptedControlCode.Shutdown;
                    if (preshutdownHandler != null) status.ControlsAccepted |= AcceptedControlCode.Preshutdown;
                    goto default;

                // clean up the device notification and don't allow any further commands
                case ServiceState.Stopped:
                    if (deviceNotificationHandle != IntPtr.Zero)
                    {
                        UnregisterDeviceNotification(deviceNotificationHandle);
                        deviceNotificationHandle = IntPtr.Zero;
                    }
                    break;

                // add all non-state changing commands and register or unregister for device notifications
                default:
                    if (parametersChangedHandler != null) status.ControlsAccepted |= AcceptedControlCode.ParamChange;
                    if (networkBindingChangedHandler != null) status.ControlsAccepted |= AcceptedControlCode.NetBindChange;
                    if (hardwareProfileEventHandler != null) status.ControlsAccepted |= AcceptedControlCode.HardwareProfileChange;
                    if (powerEventHandler != null) status.ControlsAccepted |= AcceptedControlCode.PowerEvent;
                    if (sessionChangedHandler != null) status.ControlsAccepted |= AcceptedControlCode.SessionChange;
                    if (timeChangedHandler != null) status.ControlsAccepted |= AcceptedControlCode.TimeChange;
                    if (triggerEventHandler != null) status.ControlsAccepted |= AcceptedControlCode.TriggerEvent;
                    if (userModeRebootHandler != null) status.ControlsAccepted |= AcceptedControlCode.UserModeReboot;
                    if (deviceEventHandler == null ^ deviceNotificationHandle == IntPtr.Zero)
                    {
                        if (deviceEventHandler == null)
                        {
                            var result = UnregisterDeviceNotification(deviceNotificationHandle);
                            deviceNotificationHandle = IntPtr.Zero;
                            if (!result)
                                throw new Win32Exception();
                        }
                        else
                        {
                            var filter = new DEV_BROADCAST_DEVICEINTERFACE()
                            {
                                dbcc_size = Marshal.SizeOf(typeof(DEV_BROADCAST_DEVICEINTERFACE)),
                                dbcc_devicetype = (int)DeviceEventDeviceType.Interface,
                            };
                            deviceNotificationHandle = RegisterDeviceNotification(statusHandle, ref filter, DeviceNotify.ServiceHandle | DeviceNotify.AllInterfaceClasses);
                            if (deviceNotificationHandle == IntPtr.Zero)
                            {
                                deviceEventHandler = null;
                                throw new Win32Exception();
                            }
                        }
                    }
                    break;
            }

            // notify the SCM
            if (!SetServiceStatus(statusHandle, ref status))
                throw new Win32Exception();
        }

        private static void AddEventHandler<T>(ref EventHandler<T> currentHandlers, EventHandler<T> newHandler, Version requiredWindowsVersion) where T : EventArgs
        {
            // ensure that the OS is supporting this event
            if (Environment.OSVersion.Version < requiredWindowsVersion)
                throw new PlatformNotSupportedException();
            AddEventHandler(ref currentHandlers, newHandler);
        }

        private static void AddEventHandler<T>(ref EventHandler<T> currentHandlers, EventHandler<T> newHandler) where T : EventArgs
        {
            // add the event handler and update the status
            lock (serviceLock)
            {
                currentHandlers += newHandler;
                if (statusHandle != IntPtr.Zero && status.CurrentState != ServiceState.Stopped)
                    SetAcceptedControlsAndReportStatusWithinLock();
            }
        }

        private static void RemoveEventHandler<T>(ref EventHandler<T> currentHandlers, EventHandler<T> oldHandler) where T : EventArgs
        {
            // remove the event handler and update the status
            lock (serviceLock)
            {
                currentHandlers -= oldHandler;
                if (statusHandle != IntPtr.Zero && status.CurrentState != ServiceState.Stopped)
                    SetAcceptedControlsAndReportStatusWithinLock();
            }
        }

        private static void CallEventHandler<T>(EventHandler<T> handler, T args) where T : EventArgs
        {
            // invoke the handler and forward exceptions to the thread exception handler
            try { handler(null, args); }
            catch (Exception e)
            {
                var threadExceptionHandler = ThreadException;
                if (threadExceptionHandler != null)
                {
                    var exArgs = new ThreadExceptionEventArgs(e);
                    threadExceptionHandler(null, exArgs);
                    if (exArgs.Cancel)
                        return;
                }
                throw;
            }
        }

        private static void CallWaitEventHandler<T>(EventHandler<T> handler, T args) where T : WaitEventArgs
        {
            // set the current event args
            lock (serviceLock)
            {
                // this should never happen since the method isn't reentrant, but check it anyway
                if (currentWaitEventArgs != null)
                    throw new InvalidOperationException();
                currentWaitEventArgs = args;
            }

            // call the handler
            try { CallEventHandler(handler, args); }
            finally
            {
                // clear the event args
                lock (serviceLock)
                    currentWaitEventArgs = null;
            }
        }

        private static int SetTargetStateFromSystem(ref EventHandler<PendingOperationEventArgs> handler, ServiceState state, AcceptedControlCodeEx code)
        {
            // enter the service lock
            lock (serviceLock)
            {
                // check if we can perform the operation or if we even can skip the pending action
                switch (code)
                {
                    case AcceptedControlCodeEx.Continue:
                        // ensure that we can continue and are not already running
                        if ((status.ControlsAccepted & AcceptedControlCode.PauseContinue) == 0)
                            return ERROR_CALL_NOT_IMPLEMENTED;
                        if (status.CurrentState == ServiceState.Running)
                            return NO_ERROR;

                        // skip the pending and go straight to running if the handler isn't set
                        if (continueHandler == null)
                        {
                            status.CurrentState = ServiceState.Running;
                            SetAcceptedControlsAndReportStatusWithinLock();
                            return NO_ERROR;
                        }
                        break;

                    case AcceptedControlCodeEx.Pause:
                        // ensure that we can pause and are not already paused
                        if ((status.ControlsAccepted & AcceptedControlCode.PauseContinue) == 0)
                            return ERROR_CALL_NOT_IMPLEMENTED;
                        if (status.CurrentState == ServiceState.Paused)
                            return NO_ERROR;

                        // skip the pending and go straight to paused if the handler isn't set
                        if (pauseHandler == null)
                        {
                            status.CurrentState = ServiceState.Paused;
                            SetAcceptedControlsAndReportStatusWithinLock();
                            return NO_ERROR;
                        }
                        break;

                    default:
                        // ensure that the operation is supported
                        if ((status.ControlsAccepted & (AcceptedControlCode)code) == 0)
                            return ERROR_CALL_NOT_IMPLEMENTED;
                        break;
                }

                // set the pending event handler and the new pending state and signal the main thread
                pendingEventHandler = handler;
                status.CurrentState = state;
                SetAcceptedControlsAndReportStatusWithinLock();
                serviceMainSignal.Set();
            }
            return NO_ERROR;
        }

        private static int CallEventHandlerFromSystem<T>(ref EventHandler<T> handler, T args, AcceptedControlCodeEx code) where T : EventArgs
        {
            // perform checks within the lock
            EventHandler<T> handlerCopy;
            lock (serviceLock)
            {
                // ensure that the handler is available
                switch (code)
                {
                    case AcceptedControlCodeEx.CustomCommand:
                        if (customCommandHandler == null)
                            return ERROR_CALL_NOT_IMPLEMENTED;
                        break;
                    case AcceptedControlCodeEx.DeviceEvent:
                        if (deviceNotificationHandle == IntPtr.Zero)
                            return ERROR_CALL_NOT_IMPLEMENTED;
                        break;
                    default:
                        if ((status.ControlsAccepted & (AcceptedControlCode)code) == 0)
                            return ERROR_CALL_NOT_IMPLEMENTED;
                        break;
                }

                // make a copy of the handler
                handlerCopy = handler;
            }

            // handle cancelable events immediately
            if (args is CancelEventArgs && (args as CancelEventArgs).CanBeCanceled)
            {
                CallEventHandler(handlerCopy, args);
                return (args as CancelEventArgs).Cancel ? ERROR_CANCELLED : NO_ERROR;
            }

            // queue the handler for later
            ThreadPool.QueueUserWorkItem(_ => CallEventHandler(handlerCopy, args));
            return NO_ERROR;
        }

        private static int HandlerEx(int control, int eventType, IntPtr eventData, IntPtr context)
        {
            // this should never happen, but check it anyway
            if (!initialized)
                throw new InvalidOperationException();

            // make sure RegisterServiceCtrlHandlerEx has returned
            if (statusHandle == IntPtr.Zero)
                return ERROR_NOT_READY;

            // handle custom command codes
            if (128 <= control && control <= 255)
                return CallEventHandlerFromSystem(ref customCommandHandler, new CustomCommandEventArgs(control), AcceptedControlCodeEx.CustomCommand);

            // handle other known control codes
            switch ((ServiceControl)control)
            {
                case ServiceControl.Interrogate:
                    lock (serviceLock)
                        return SetServiceStatus(statusHandle, ref status) ? NO_ERROR : Marshal.GetLastWin32Error();
                case ServiceControl.Stop:
                    return SetTargetStateFromSystem(ref stopHandler, ServiceState.StopPending, AcceptedControlCodeEx.Stop);
                case ServiceControl.Pause:
                    return SetTargetStateFromSystem(ref pauseHandler, ServiceState.PausePending, AcceptedControlCodeEx.Pause);
                case ServiceControl.Continue:
                    return SetTargetStateFromSystem(ref continueHandler, ServiceState.ContinuePending, AcceptedControlCodeEx.Continue);
                case ServiceControl.Shutdown:
                    return SetTargetStateFromSystem(ref shutdownHandler, ServiceState.StopPending, AcceptedControlCodeEx.Shutdown);
                case ServiceControl.Preshutdown:
                    return SetTargetStateFromSystem(ref preshutdownHandler, ServiceState.StopPending, AcceptedControlCodeEx.Preshutdown);
                case ServiceControl.ParamChange:
                    return CallEventHandlerFromSystem(ref parametersChangedHandler, EventArgs.Empty, AcceptedControlCodeEx.ParamChange);
                case ServiceControl.NetBindAdd:
                    return CallEventHandlerFromSystem(ref networkBindingChangedHandler, new NetworkBindingChangedEventArgs(NetworkBindingChangedReason.Add), AcceptedControlCodeEx.NetBindChange);
                case ServiceControl.NetBindRemove:
                    return CallEventHandlerFromSystem(ref networkBindingChangedHandler, new NetworkBindingChangedEventArgs(NetworkBindingChangedReason.Remove), AcceptedControlCodeEx.NetBindChange);
                case ServiceControl.NetBindEnable:
                    return CallEventHandlerFromSystem(ref networkBindingChangedHandler, new NetworkBindingChangedEventArgs(NetworkBindingChangedReason.Enable), AcceptedControlCodeEx.NetBindChange);
                case ServiceControl.NetBindDisable:
                    return CallEventHandlerFromSystem(ref networkBindingChangedHandler, new NetworkBindingChangedEventArgs(NetworkBindingChangedReason.Disable), AcceptedControlCodeEx.NetBindChange);
                case ServiceControl.DeviceEvent:
                    return CallEventHandlerFromSystem(ref deviceEventHandler, DeviceEventArgs.FromNative(eventType, eventData), AcceptedControlCodeEx.DeviceEvent);
                case ServiceControl.HardwareProfileChange:
                    return CallEventHandlerFromSystem(ref hardwareProfileEventHandler, HardwareProfileEventArgs.FromNative(eventType, eventData), AcceptedControlCodeEx.HardwareProfileChange);
                case ServiceControl.PowerEvent:
                    return CallEventHandlerFromSystem(ref powerEventHandler, PowerEventArgs.FromNative(eventType, eventData), AcceptedControlCodeEx.PowerEvent);
                case ServiceControl.SessionEvent:
                    return CallEventHandlerFromSystem(ref sessionChangedHandler, SessionChangedEventArgs.FromNative(eventType, eventData), AcceptedControlCodeEx.SessionChange);
                case ServiceControl.TimeChange:
                    return CallEventHandlerFromSystem(ref timeChangedHandler, TimeChangedEventArgs.FromNative(eventType, eventData), AcceptedControlCodeEx.TimeChange);
                case ServiceControl.TriggerEvent:
                    return CallEventHandlerFromSystem(ref triggerEventHandler, EventArgs.Empty, AcceptedControlCodeEx.TriggerEvent);
                case ServiceControl.UserModeReboot:
                    return CallEventHandlerFromSystem(ref userModeRebootHandler, EventArgs.Empty, AcceptedControlCodeEx.UserModeReboot);
            }

            // must be something new
            return ERROR_CALL_NOT_IMPLEMENTED;
        }

        private static void ServiceMain(int argc, string[] argv)
        {
            // this should never happen, but check it anyway
            if (!initialized)
                throw new InvalidOperationException();

            // initialize the service itself
            lock (serviceLock)
            {
                // again, shouldn't happen, but what the heck
                if (statusHandle != IntPtr.Zero)
                    throw new InvalidOperationException();

                // initialize the status structure
                status.ServiceType = ServiceType.Win32OwnProcess;
                status.CurrentState = quitServiceMain ? ServiceState.Stopped : ServiceState.StartPending;
                status.CheckPoint = 0;
                status.WaitHint = 0;

                // register the handle
                statusHandle = RegisterServiceCtrlHandlerEx(mainServiceName, HandlerEx, IntPtr.Zero);
                if (statusHandle == IntPtr.Zero)
                    throw new Win32Exception();

                // report the status
                SetAcceptedControlsAndReportStatusWithinLock();

                // if we're already stopped, return immediately
                if (quitServiceMain)
                    return;
            }

            // handle state changes
            do
            {
                // shutdown the service if requested
                if (quitServiceMain)
                {
                    lock (serviceLock)
                    {
                        status.CurrentState = ServiceState.Stopped;
                        SetAcceptedControlsAndReportStatusWithinLock();
                    }
                    break;
                }

                // get the current state within a lock
                ServiceState current;
                lock (serviceLock)
                    current = status.CurrentState;

                // call the necessary event handler or continue with the loop if nothing needs to be done
                switch (status.CurrentState)
                {
                    case ServiceState.StartPending:
                        if (startHandler != null) // NOTE: no copy necessary since initialized = true
                            CallWaitEventHandler(startHandler, new StartEventArgs(argv ?? new string[] { mainServiceName }));
                        current = ServiceState.Running;
                        break;
                    case ServiceState.PausePending:
                        CallWaitEventHandler(pendingEventHandler, new PendingOperationEventArgs());
                        current = ServiceState.Paused;
                        break;
                    case ServiceState.ContinuePending:
                        CallWaitEventHandler(pendingEventHandler, new PendingOperationEventArgs());
                        current = ServiceState.Running;
                        break;
                    case ServiceState.StopPending:
                        CallWaitEventHandler(pendingEventHandler, new PendingOperationEventArgs());
                        current = ServiceState.Stopped;
                        break;
                    default:
                        continue;
                }

                lock (serviceLock)
                {
                    // set and report the new status
                    status.CurrentState = current;
                    status.CheckPoint = 0;
                    status.WaitHint = 0;
                    SetAcceptedControlsAndReportStatusWithinLock();

                    // quit if there's nothing left to do
                    if (current == ServiceState.Stopped)
                        break;
                }
            }
            while (serviceMainSignal.WaitOne());
        }

        /// <summary>
        /// Gets the native status handle.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Advanced)]
        public static IntPtr StatusHandle { get { return statusHandle; } }

        /// <summary>
        /// Registers the service to receive power setting notifications for the specific power setting event.
        /// </summary>
        /// <param name="powerSettingGuid">The unique power setting identifier.</param>
        /// <exception cref="InvalidOperationException">If the service hasn't been started yet.</exception>
        /// <exception cref="PlatformNotSupportedException">On Windows Server 2003 and Windows XP.</exception>
        public static void RegisterPowerSettingNotification(Guid powerSettingGuid)
        {
            powerSettingNotifications.Register(powerSettingGuid, _ => _, RegisterPowerSettingNotification, new Version(6, 0));
        }

        /// <summary>
        /// Unregisters the power setting notification.
        /// </summary>
        /// <param name="powerSettingGuid">The unique power setting identifier.</param>
        /// <returns><c>true</c> if the last notification was successfully unregistered, <c>false</c> otherwise.</returns>
        public static bool UnregisterPowerSettingNotification(Guid powerSettingGuid)
        {
            return powerSettingNotifications.Unregister(powerSettingGuid, UnregisterPowerSettingNotification);
        }

        /// <summary>
        /// Registers the device handle for which the service will receive notifications.
        /// </summary>
        /// <param name="deviceHandle">A handle to the device.</param>
        /// <exception cref="InvalidOperationException">If the service hasn't been started yet.</exception>
        public static void RegisterDeviceHandleNotification(IntPtr deviceHandle)
        {
            deviceHandleNotifications.Register
            (
                deviceHandle,
                handle => new DEV_BROADCAST_HANDLE()
                {
                    dbcc_size = Marshal.SizeOf(typeof(DEV_BROADCAST_HANDLE)),
                    dbcc_devicetype = (int)DeviceEventDeviceType.Handle,
                    dbch_handle = handle
                },
                RegisterDeviceNotification
            );
        }

        /// <summary>
        /// Unregisters the device handle notification.
        /// </summary>
        /// <param name="deviceHandle">A handle to the device.</param>
        /// <returns><c>true</c> if the last notification was successfully unregistered, <c>false</c> otherwise.</returns>
        public static bool UnregisterDeviceHandleNotification(IntPtr deviceHandle)
        {
            return deviceHandleNotifications.Unregister(deviceHandle, UnregisterDeviceNotification);
        }

        /// <summary>
        /// Logs the given event.
        /// </summary>
        /// <param name="type">The severity of the event.</param>
        /// <param name="message">A <see cref="string"/> describing the event.</param>
        /// <exception cref="InvalidOperationException">If <see cref="ServiceApplication.Run"/> hasn't been called yet.</exception>
        /// <exception cref="InvalidEnumArgumentException">If <paramref name="type"/> is invalid.</exception>
        /// <exception cref="ArgumentNullException">If <paramref name="message"/> is <c>null</c>.</exception>
        /// <exception cref="StackOverflowException"/>
        /// <exception cref="OutOfMemoryException"/>
        /// <exception cref="ThreadAbortException"/>
        public static void LogEvent(EventLogEntryType type, string message)
        {
            // check the input
            CheckEnum("type", type);
            if (message == null)
                throw new ArgumentNullException("message");

            // check if initialized
            if (!initialized)
                throw new InvalidOperationException();

            // ellipse the message if necessary
            const string ellipse = "...";
            const int maxLen = 0x7ffe;
            if (message.Length > maxLen)
                message = message.Substring(0, maxLen - ellipse.Length) + ellipse;

#if DEBUG
            // determine the color 
            var color = ConsoleColor.Green;
            switch (type)
            {
                case EventLogEntryType.FailureAudit:
                    color = ConsoleColor.Red;
                    goto case EventLogEntryType.SuccessAudit;
                case EventLogEntryType.SuccessAudit:
                    message = "[AUDIT] " + message;
                    break;
                case EventLogEntryType.Error:
                    color = ConsoleColor.Red;
                    break;
                case EventLogEntryType.Warning:
                    color = ConsoleColor.Yellow;
                    break;
            }
#endif

            // always catch and swallow any non-fatal exceptions
            try
            {
#if DEBUG
                DebugWriteLine(message, color);
#else
                log.WriteEntry(message, type);
#endif
            }
            catch (StackOverflowException) { throw; }
            catch (OutOfMemoryException) { throw; }
            catch (ThreadAbortException) { throw; }
            catch { }
        }

        /// <summary>
        /// Logs the given event after formatting its input string.
        /// </summary>
        /// <param name="type">The severity of the event.</param>
        /// <param name="format">The input format.</param>
        /// <param name="args">All parameters referenced in the <paramref name="format"/> string.</param>
        /// <exception cref="FormatException"><paramref name="format"/> is invalid or the index of a format item is less than zero, or greater than or equal to the length of the <paramref name="args"/> array.</exception>
        /// <exception cref="InvalidOperationException">If <see cref="ServiceApplication.Run"/> hasn't been called yet.</exception>
        /// <exception cref="StackOverflowException"/>
        /// <exception cref="OutOfMemoryException"/>
        /// <exception cref="ThreadAbortException"/>
        public static void LogEvent(EventLogEntryType type, string format, params object[] args)
        {
            LogEvent(type, string.Format(format, args));
        }

        /// <summary>
        /// Initializes the service and forwards control to the service dispatcher.
        /// </summary>
        /// <param name="serviceName">The optional custom name for this service. If omitted, the entry <see cref="Assembly"/>'s name will be used. Also see the remarks.</param>
        /// <exception cref="InvalidOperationException">This method has already been called.</exception>
        /// <remarks>
        /// If the service is registered as own-process (which it has to be), <paramref name="serviceName"/> is ignored by Windows and only used for logging.
        /// All event handlers must be hooked up before this method is called.
        /// </remarks>
        public static void Run(string serviceName = null)
        {
            // acquire the initialize lock
            lock (initializeLock)
            {
                // make sure this function hasn't already been called
                if (initialized)
                    throw new InvalidOperationException();

                // if exit was called, bail
                if (exitPrematurely)
                    return;

                // prepare the service name
                mainServiceName = serviceName ?? Assembly.GetEntryAssembly().GetName().Name;

#if DEBUG
                // clear the console
                Console.BackgroundColor = ConsoleColor.Black;
                Console.ForegroundColor = ConsoleColor.White;
                Console.Clear();
                Console.TreatControlCAsInput = true;
#else
                // open (and create if necessary) the event log soure
                const string logName = "Application";
                if (!EventLog.SourceExists(mainServiceName))
                    EventLog.CreateEventSource(mainServiceName, logName);
                log = new EventLog(logName, ".", mainServiceName);
#endif

                // set the initialize flag
                initialized = true;
            }

            // log all unhandled exceptions
            AppDomain.CurrentDomain.UnhandledException += (sender, e) => LogEvent(EventLogEntryType.Error, e.ExceptionObject.ToString());

            // run the service dispatcher
            if
            (
                !StartServiceCtrlDispatcher
                (
                    new SERVICE_TABLE_ENTRY[]
                    {
                        new SERVICE_TABLE_ENTRY(mainServiceName, ServiceMain),
                        new SERVICE_TABLE_ENTRY(null, null)
                    }
                )
            )
                throw new Win32Exception();
        }

        /// <summary>
        /// Shuts down the <see cref="ServiceApplication"/> in a controlled fashion.
        /// </summary>
        /// <param name="exitCode">An optional Win32 error code.</param>
        /// <remarks>
        /// This will not trigger Windows fault handling, even if <paramref name="exitCode"/> isn't <c>0</c>.
        /// </remarks>
        public static void Exit(int exitCode = 0)
        {
            // if the service has not been started yet, shutdown the entire runtime
            lock (initializeLock)
            {
                if (!initialized)
                {
                    exitPrematurely = true;
                    Environment.ExitCode = exitCode;
                }
            }

            // otherwise set the exit code and tell the service main to quit
            status.Win32ExitCode = exitCode;
            quitServiceMain = true;
            serviceMainSignal.Set();
        }

        /// <summary>
        /// Occurs when the service is resumed. 
        /// </summary>
        public static event EventHandler<PendingOperationEventArgs> Continue
        {
            add { AddEventHandler(ref continueHandler, value); }
            remove { RemoveEventHandler(ref continueHandler, value); }
        }

        /// <summary>
        /// Occurs when a networking binding has changed.
        /// </summary>
        public static event EventHandler<NetworkBindingChangedEventArgs> NetworkBindingChanged
        {
            add { AddEventHandler(ref networkBindingChangedHandler, value); }
            remove { RemoveEventHandler(ref networkBindingChangedHandler, value); }
        }

        /// <summary>
        /// Occurs when the service's service-specific startup parameters have changed.
        /// </summary>
        public static event EventHandler<EventArgs> ParametersChanged
        {
            add { AddEventHandler(ref parametersChangedHandler, value); }
            remove { RemoveEventHandler(ref parametersChangedHandler, value); }
        }

        /// <summary>
        /// Occurs the service is suspended.
        /// </summary>
        public static event EventHandler<PendingOperationEventArgs> Pause
        {
            add { AddEventHandler(ref pauseHandler, value); }
            remove { RemoveEventHandler(ref pauseHandler, value); }
        }

        /// <summary>
        /// Occurs when the system will be shutting down.
        /// Services that need additional time to perform cleanup tasks beyond the tight time restriction at system shutdown can use this notification.
        /// </summary>
        /// <exception cref="PlatformNotSupportedException">On Windows Server 2003 and Windows XP.</exception>
        /// <exception cref="InvalidOperationException">If <see cref="ServiceApplication.Shutdown"/> has already been registered.</exception>
        public static event EventHandler<PendingOperationEventArgs> Preshutdown
        {
            add
            {
                lock (serviceLock)
                {
                    if (shutdownHandler != null)
                        throw new InvalidOperationException();
                    AddEventHandler(ref preshutdownHandler, value, new Version(6, 0));
                }
            }
            remove { RemoveEventHandler(ref preshutdownHandler, value); }
        }

        /// <summary>
        /// Occurs when the machine is about to shut down.
        /// </summary>
        /// <exception cref="InvalidOperationException">If <see cref="ServiceApplication.Preshutdown"/> has already been registered.</exception>
        public static event EventHandler<PendingOperationEventArgs> Shutdown
        {
            add
            {
                lock (serviceLock)
                {
                    if (preshutdownHandler != null)
                        throw new InvalidOperationException();
                    AddEventHandler(ref shutdownHandler, value);
                }
            }
            remove { RemoveEventHandler(ref shutdownHandler, value); }
        }

        /// <summary>
        /// Occurs when the service should be stopped.
        /// </summary>
        public static event EventHandler<PendingOperationEventArgs> Stop
        {
            add { AddEventHandler(ref stopHandler, value); }
            remove { RemoveEventHandler(ref stopHandler, value); }
        }

        /// <summary>
        /// Occurs when a device is plugged in or unplugged.
        /// </summary>
        public static event EventHandler<DeviceEventArgs> DeviceEvent
        {
            add { AddEventHandler(ref deviceEventHandler, value); }
            remove { RemoveEventHandler(ref deviceEventHandler, value); }
        }

        /// <summary>
        /// Occurs when the computer's hardware profile has changed.
        /// </summary>
        public static event EventHandler<HardwareProfileEventArgs> HardwareProfileEvent
        {
            add { AddEventHandler(ref hardwareProfileEventHandler, value); }
            remove { RemoveEventHandler(ref hardwareProfileEventHandler, value); }
        }

        /// <summary>
        /// Occurs when the machine is suspended or resumed.
        /// </summary>
        public static event EventHandler<PowerEventArgs> PowerEvent
        {
            add { AddEventHandler(ref powerEventHandler, value); }
            remove { RemoveEventHandler(ref powerEventHandler, value); }
        }

        /// <summary>
        /// Occurs when a users logs on or off.
        /// </summary>
        public static event EventHandler<SessionChangedEventArgs> SessionChanged
        {
            add { AddEventHandler(ref sessionChangedHandler, value); }
            remove { RemoveEventHandler(ref sessionChangedHandler, value); }
        }

        /// <summary>
        /// Occurs when the system time has changed.
        /// </summary>
        /// <exception cref="PlatformNotSupportedException">On Windows Server 2008, Windows Vista, Windows Server 2003, and Windows XP.</exception>
        public static event EventHandler<TimeChangedEventArgs> TimeChanged
        {
            add { AddEventHandler(ref timeChangedHandler, value, new Version(6, 1)); }
            remove { RemoveEventHandler(ref timeChangedHandler, value); }
        }

        /// <summary>
        /// Occurs when a service trigger event that the event has occurred.
        /// </summary>
        /// <exception cref="PlatformNotSupportedException">On Windows Server 2008, Windows Vista, Windows Server 2003, and Windows XP.</exception>
        public static event EventHandler<EventArgs> TriggerEvent
        {
            add { AddEventHandler(ref triggerEventHandler, value, new Version(6, 1)); }
            remove { RemoveEventHandler(ref triggerEventHandler, value); }
        }

        /// <summary>
        /// Occurs when the user has initiated a reboot.
        /// </summary>
        /// <exception cref="PlatformNotSupportedException">On Windows Server 2008 R2, Windows 7, Windows Server 2008, Windows Vista, Windows Server 2003, and Windows XP.</exception>
        public static event EventHandler<EventArgs> UserModeReboot
        {
            add { AddEventHandler(ref userModeRebootHandler, value, new Version(6, 2)); }
            remove { RemoveEventHandler(ref userModeRebootHandler, value); }
        }

        /// <summary>
        /// Occurs when an application sends a custom command to the service.
        /// </summary>
        public static event EventHandler<CustomCommandEventArgs> CustomCommand
        {
            add { AddEventHandler(ref customCommandHandler, value); }
            remove { RemoveEventHandler(ref customCommandHandler, value); }
        }

        /// <summary>
        /// Occurs after the service has been started.
        /// </summary>
        /// <exception cref="InvalidOperationException">If <see cref="ServiceApplication.Run"/> has already been called.</exception>
        public static event EventHandler<StartEventArgs> Start
        {
            add
            {
                lock (initializeLock)
                {
                    if (initialized)
                        throw new InvalidOperationException();
                    startHandler += value;
                }
            }
            remove
            {
                lock (initializeLock)
                {
                    if (initialized)
                        throw new InvalidOperationException();
                    startHandler -= value;
                }
            }
        }

        /// <summary>
        /// Occurs after an exception has been thrown.
        /// </summary>
        public static event EventHandler<ThreadExceptionEventArgs> ThreadException;
    }
}
