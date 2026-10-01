using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace FarmoraTray.Printing;

internal readonly record struct DeviceNode(string InstanceId, string? FriendlyName, Guid? ContainerId, bool IsPresent);

internal static class NativeDeviceConnection
{
    private const int DigcfPresent = 0x00000002;
    private const int DigcfAllClasses = 0x00000004;
    private const int DigcfDeviceInterface = 0x00000010;
    private const int ErrorNoMoreItems = 259;
    private const int KeyQueryValue = 0x0001;
    private const uint DevpropTypeGuid = 0x0000000D;
    private const uint DevpropTypeBoolean = 0x00000011;
    private const uint DevpropTypeString = 0x00000012;
    private const int MaxPropertyBytes = 64 * 1024;

    private static readonly IntPtr InvalidHandle = new(-1);
    private static readonly Guid UsbPrintInterface = new("28d78fad-5a12-11d1-ae5b-0000f803a8c2");
    private static readonly DevPropKey FriendlyNameKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly DevPropKey ContainerIdKey = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    private static readonly DevPropKey IsPresentKey = new(new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 5);

    public static IReadOnlyList<DeviceNode> ListDevices()
    {
        var set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DigcfAllClasses);
        if (set == InvalidHandle)
        {
            throw new InvalidOperationException(
                $"SetupDiGetClassDevs failed (Win32 {Marshal.GetLastWin32Error()}).");
        }

        try
        {
            var devices = new List<DeviceNode>();
            var data = new SpDevinfoData { CbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
            for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref data); index++)
            {
                var instanceId = GetInstanceId(set, ref data);
                if (instanceId is null)
                {
                    continue;
                }

                devices.Add(new DeviceNode(
                    instanceId,
                    GetString(set, ref data, FriendlyNameKey),
                    GetGuid(set, ref data, ContainerIdKey),
                    GetBoolean(set, ref data, IsPresentKey) ?? true));
            }

            return devices;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    public static IReadOnlySet<string>? TryListUsbPrintPorts()
    {
        var interfaceGuid = UsbPrintInterface;
        var set = SetupDiGetClassDevs(ref interfaceGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == InvalidHandle)
        {
            return null;
        }

        try
        {
            var ports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var data = new SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
            uint index = 0;
            for (; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref interfaceGuid, index, ref data); index++)
            {
                var port = ReadUsbPortName(set, ref data);
                if (port is null)
                {
                    return null;
                }

                ports.Add(port);
            }

            return Marshal.GetLastWin32Error() == ErrorNoMoreItems ? ports : null;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static string? ReadUsbPortName(IntPtr set, ref SpDeviceInterfaceData data)
    {
        var handle = SetupDiOpenDeviceInterfaceRegKey(set, ref data, 0, KeyQueryValue);
        if (handle == InvalidHandle || handle == IntPtr.Zero)
        {
            return null;
        }

        using var key = RegistryKey.FromHandle(new SafeRegistryHandle(handle, ownsHandle: true));
        var baseName = key.GetValue("Base Name") as string;
        if (string.IsNullOrWhiteSpace(baseName) || key.GetValue("Port Number") is not int number || number < 0)
        {
            return null;
        }

        return $"{baseName.Trim()}{number:D3}";
    }

    private static string? GetInstanceId(IntPtr set, ref SpDevinfoData data)
    {
        var buffer = new StringBuilder(512);
        if (SetupDiGetDeviceInstanceId(set, ref data, buffer, buffer.Capacity, out var required))
        {
            return buffer.ToString();
        }

        if (required <= buffer.Capacity || required > MaxPropertyBytes)
        {
            return null;
        }

        buffer = new StringBuilder(required);
        return SetupDiGetDeviceInstanceId(set, ref data, buffer, buffer.Capacity, out _)
            ? buffer.ToString()
            : null;
    }

    private static string? GetString(IntPtr set, ref SpDevinfoData data, DevPropKey key)
    {
        var bytes = GetProperty(set, ref data, key, DevpropTypeString);
        return bytes is null ? null : Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static Guid? GetGuid(IntPtr set, ref SpDevinfoData data, DevPropKey key)
    {
        var bytes = GetProperty(set, ref data, key, DevpropTypeGuid);
        return bytes is { Length: >= 16 } ? new Guid(bytes.AsSpan(0, 16)) : null;
    }

    private static bool? GetBoolean(IntPtr set, ref SpDevinfoData data, DevPropKey key)
    {
        var bytes = GetProperty(set, ref data, key, DevpropTypeBoolean);
        return bytes is { Length: >= 1 } ? bytes[0] != 0 : null;
    }

    private static byte[]? GetProperty(IntPtr set, ref SpDevinfoData data, DevPropKey key, uint expectedType)
    {
        SetupDiGetDeviceProperty(set, ref data, ref key, out _, null, 0, out var required, 0);
        if (required <= 0 || required > MaxPropertyBytes)
        {
            return null;
        }

        var buffer = new byte[required];
        if (!SetupDiGetDeviceProperty(set, ref data, ref key, out var type, buffer, buffer.Length, out _, 0)
            || type != expectedType)
        {
            return null;
        }

        return buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public DevPropKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDeviceInterfaceRegKey(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        uint reserved,
        int samDesired);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        StringBuilder deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)]
    private static extern bool SetupDiGetDeviceProperty(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        ref DevPropKey propertyKey,
        out uint propertyType,
        byte[]? propertyBuffer,
        int propertyBufferSize,
        out int requiredSize,
        uint flags);
}
