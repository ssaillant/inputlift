// Copyright (c) 2026 InputLift contributors
// SPDX-License-Identifier: MIT
// Original MIT-licensed interop helper. No code runs merely by loading this type.
// The private refresh request is derived from offline inspection of Microsoft's
// signed BTP 1.14.0 btvs.exe. It is not a documented supported Windows API.
// Only an explicitly approved live run may call ApplyRefresh. A true Windows
// Restart is required afterward: removal of the registry flag is not proof
// that the tested driver's cached sensitive-logging flag was cleared.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal static class BluetoothLoggingRefresh
{
    private static readonly Guid RadioInterface = new Guid("0850302A-B344-4FDA-9BE9-90576B8D46F0");
    private const uint RefreshControlCode = 0x00411008;
    private const uint LoggingUpdateMask = 0x00001000; // Not SSP/SMP debug mask 0x2000.
    private static readonly IntPtr InvalidHandle = new IntPtr(-1);

    internal sealed class RadioIdentity
    {
        internal string InterfacePath { get; private set; }
        internal string InstanceId { get; private set; }
        internal RadioIdentity(string path, string instance) { InterfacePath=path; InstanceId=instance; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceData
    {
        internal uint Size;
        internal Guid InterfaceClassGuid;
        internal uint Flags;
        internal IntPtr Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        internal uint Size;
        internal Guid ClassGuid;
        internal uint DevInst;
        internal IntPtr Reserved;
    }
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, string enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devices, IntPtr device, ref Guid guid, uint index, ref DeviceInterfaceData data);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr devices, ref DeviceInterfaceData data,
        IntPtr detail, uint detailSize, out uint required, ref DeviceInfoData info);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr devices, ref DeviceInfoData info,
        StringBuilder instance, uint capacity, out uint required);
    [DllImport("setupapi.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, ref uint input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

    private static Exception NativeFailure(string operation, int code)
    {
        return new Win32Exception(code, operation+" failed with Win32 "+code+": "+new Win32Exception(code).Message);
    }
    private static bool ExpectedInstance(string instance)
    {
        return instance!=null && instance.StartsWith(@"USB\VID_8087&PID_0AAA\",StringComparison.OrdinalIgnoreCase)
            && instance.Length>@"USB\VID_8087&PID_0AAA\".Length && instance.Length<1024;
    }
    private static bool PlausiblePath(string path)
    {
        return path!=null && path.StartsWith(@"\\?\",StringComparison.Ordinal) && path.Length>4 && path.Length<32768;
    }
    private static bool SameIdentity(RadioIdentity a, RadioIdentity b)
    {
        return a!=null && b!=null && ExpectedInstance(a.InstanceId) && ExpectedInstance(b.InstanceId)
            && PlausiblePath(a.InterfacePath) && PlausiblePath(b.InterfacePath)
            && string.Equals(a.InstanceId,b.InstanceId,StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.InterfacePath,b.InterfacePath,StringComparison.OrdinalIgnoreCase);
    }

    // PnP metadata only. No radio handle is opened and no IOCTL is sent here.
    internal static RadioIdentity InspectSingleExpectedRadio()
    {
        if(IntPtr.Size!=8) throw new InvalidOperationException("This reviewed helper requires an x64 process.");
        Guid guid=RadioInterface;
        IntPtr devices=SetupDiGetClassDevsW(ref guid,null,IntPtr.Zero,0x12); // PRESENT | DEVICEINTERFACE.
        if(devices==InvalidHandle) throw NativeFailure("Enumerate Bluetooth radio interfaces",Marshal.GetLastWin32Error());
        try
        {
            List<RadioIdentity> found=new List<RadioIdentity>();
            for(uint index=0;index<16;index++)
            {
                DeviceInterfaceData data=new DeviceInterfaceData { Size=(uint)Marshal.SizeOf(typeof(DeviceInterfaceData)) };
                if(!SetupDiEnumDeviceInterfaces(devices,IntPtr.Zero,ref guid,index,ref data))
                {
                    int error=Marshal.GetLastWin32Error();
                    if(error==259)
                    {
                        if(found.Count!=1) throw new InvalidOperationException("Expected exactly one present Bluetooth radio interface; found "+found.Count+". No refresh sent.");
                        RadioIdentity only=found[0];
                        if(!ExpectedInstance(only.InstanceId) || !PlausiblePath(only.InterfacePath))
                            throw new InvalidOperationException("Present Bluetooth interface is not the expected Intel USB VID_8087/PID_0AAA radio: "+only.InstanceId+". No refresh sent.");
                        return only;
                    }
                    throw NativeFailure("Enumerate Bluetooth interface index "+index,error);
                }
                if(data.InterfaceClassGuid!=guid) throw new InvalidOperationException("Unexpected radio interface GUID; no refresh sent.");
                DeviceInfoData info=new DeviceInfoData { Size=(uint)Marshal.SizeOf(typeof(DeviceInfoData)) };
                uint required;
                bool first=SetupDiGetDeviceInterfaceDetailW(devices,ref data,IntPtr.Zero,0,out required,ref info);
                int measureError=Marshal.GetLastWin32Error();
                if(first || measureError!=122) throw NativeFailure("Measure Bluetooth interface detail",measureError);
                if(required<8 || required>65536) throw new InvalidOperationException("Unexpected Bluetooth interface detail length; no refresh sent.");
                IntPtr detail=Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail,8); // SP_DEVICE_INTERFACE_DETAIL_DATA_W cbSize on x64.
                    if(!SetupDiGetDeviceInterfaceDetailW(devices,ref data,detail,required,out required,ref info))
                        throw NativeFailure("Read Bluetooth interface detail",Marshal.GetLastWin32Error());
                    string path=Marshal.PtrToStringUni(IntPtr.Add(detail,4));
                    StringBuilder instance=new StringBuilder(1024);
                    uint instanceRequired;
                    if(!SetupDiGetDeviceInstanceIdW(devices,ref info,instance,(uint)instance.Capacity,out instanceRequired))
                        throw NativeFailure("Read Bluetooth radio instance ID",Marshal.GetLastWin32Error());
                    found.Add(new RadioIdentity(path,instance.ToString()));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
            throw new InvalidOperationException("Bluetooth interface enumeration exceeded its bound; no refresh sent.");
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
    }

    // Synchronous call: the independent guardian, not this thread, owns the
    // registry-restoration watchdog. This call may block; cancellation and a
    // bounded whole-program duration cannot be promised for a private driver API.
    internal static void ApplyRefresh(RadioIdentity expected, Action beforeRequest)
    {
        if(beforeRequest==null) throw new ArgumentNullException("beforeRequest");
        if(!SameIdentity(expected,expected)) throw new InvalidOperationException("Invalid pinned radio identity; no refresh sent.");
        RadioIdentity current=InspectSingleExpectedRadio();
        if(!SameIdentity(expected,current)) throw new InvalidOperationException("Bluetooth radio identity changed; no refresh sent.");
        using(SafeFileHandle radio=CreateFileW(current.InterfacePath,0xC0000000,3,IntPtr.Zero,3,0,IntPtr.Zero))
        {
            if(radio.IsInvalid) throw NativeFailure("Open pinned Bluetooth radio for logging refresh",Marshal.GetLastWin32Error());
            uint input=LoggingUpdateMask,returned;
            beforeRequest(); // Durable ownership/possible-activation proof before entering kernel.
            if(!DeviceIoControl(radio,RefreshControlCode,ref input,4,IntPtr.Zero,0,out returned,IntPtr.Zero))
                throw NativeFailure("Bluetooth logging refresh (0x00411008, mask 0x1000)",Marshal.GetLastWin32Error());
            if(returned!=0) throw new InvalidOperationException("Bluetooth logging refresh returned an unexpected output length: "+returned+". Restart remains required.");
        }
    }

    internal static string[] SelfTestErrors()
    {
        List<string> errors=new List<string>();
        Action<bool,string> check=delegate(bool okay,string text) { if(!okay) errors.Add(text); };
        check(IntPtr.Size==8 && Marshal.SizeOf(typeof(DeviceInterfaceData))==32,"x64 device-interface ABI");
        check(Marshal.SizeOf(typeof(DeviceInfoData))==32 && Marshal.OffsetOf(typeof(DeviceInfoData),"Reserved").ToInt32()==24,"x64 device-info ABI");
        check(RefreshControlCode==((0x41u<<16)|(0x402u<<2)),"Exact buffered, ANY_ACCESS Bluetooth refresh control code");
        check(LoggingUpdateMask==0x1000 && LoggingUpdateMask!=0x2000,"Only logging mask; no pairing/debug mask");
        check(ExpectedInstance(@"USB\VID_8087&PID_0AAA\TEST"),"Expected Intel instance accepted");
        check(ExpectedInstance(@"usb\vid_8087&pid_0aaa\test"),"Case-insensitive instance comparison");
        check(!ExpectedInstance(@"USB\VID_8087&PID_0AAA\"),"Empty instance suffix rejected");
        check(!ExpectedInstance(@"USB\VID_8087&PID_0AAA_WRONG\TEST"),"Partial PID match rejected");
        check(!ExpectedInstance(@"HID\VID_8087&PID_0AAA\TEST"),"Keyboard/device-class confusion rejected");
        RadioIdentity a=new RadioIdentity(@"\\?\USB#TEST",@"USB\VID_8087&PID_0AAA\TEST");
        check(SameIdentity(a,new RadioIdentity(@"\\?\usb#test",@"usb\vid_8087&pid_0aaa\test")),"Identical pinned identity accepted");
        check(!SameIdentity(a,new RadioIdentity(@"\\?\USB#OTHER",a.InstanceId)),"Changed interface path rejected");
        check(!SameIdentity(a,new RadioIdentity(a.InterfacePath,@"USB\VID_8087&PID_0AAA\OTHER")),"Changed physical instance rejected");
        check(!SameIdentity(a,new RadioIdentity(@"C:\arbitrary",a.InstanceId)),"Ordinary file path rejected");
        check(!SameIdentity(null,a),"Missing identity rejected");
        return errors.ToArray();
    }
}
