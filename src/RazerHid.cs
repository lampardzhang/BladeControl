// Protocol adapted from MIT-licensed Fatalution/r-helper (librazer).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace BladeControl {
public interface IRazerHid : IDisposable {
    byte[] Mode(byte zone);
    int Rpm(byte zone,bool actual);
    void SetMode(byte zone,byte performance,byte fan);
    void SetRpm(byte zone,int rpm);
    void Auto();
}
public sealed class RazerHid : IRazerHid {
    SafeFileHandle handle;
    byte sequence;
    public string Path { get; private set; }
    [StructLayout(LayoutKind.Sequential)] struct InterfaceData { public int Size; public Guid Class; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct Caps {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst=17)] public ushort[] Reserved;
        public ushort LinkNodes, InputButtons, InputValues, InputData, OutputButtons, OutputValues, OutputData, FeatureButtons, FeatureValues, FeatureData;
    }
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError=true)] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll", SetLastError=true)] static extern bool HidD_SetFeature(SafeFileHandle h, byte[] data, int length);
    [DllImport("hid.dll", SetLastError=true)] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] data, int length);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr SetupDiGetClassDevs(ref Guid g, string e, IntPtr p, uint f);
    [DllImport("setupapi.dll", SetLastError=true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref InterfaceData d, IntPtr b, uint n, out uint needed, IntPtr dev);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeFileHandle CreateFile(string p, uint a, uint share, IntPtr sec, uint creation, uint flags, IntPtr template);
    public static string Model {
        get { using(var key=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS")) return Convert.ToString(key.GetValue("SystemProductName")); }
    }
    public static RazerHid Open() {
        if (!Model.Contains("RZ09-0528")) throw new InvalidOperationException("此版本仅适配 Blade 16 2025 / RZ09-0528。");
        Guid g; HidD_GetHidGuid(out g);
        IntPtr set=SetupDiGetClassDevs(ref g,null,IntPtr.Zero,18);
        if(set==new IntPtr(-1)) throw new Win32Exception();
        var errors=new List<string>();
        try {
            for(uint i=0;;i++) {
                var data=new InterfaceData(); data.Size=Marshal.SizeOf(data);
                if(!SetupDiEnumDeviceInterfaces(set,IntPtr.Zero,ref g,i,ref data)) break;
                uint size; SetupDiGetDeviceInterfaceDetail(set,ref data,IntPtr.Zero,0,out size,IntPtr.Zero);
                IntPtr buffer=Marshal.AllocHGlobal((int)size);
                try {
                    Marshal.WriteInt32(buffer,IntPtr.Size==8?8:6);
                    if(!SetupDiGetDeviceInterfaceDetail(set,ref data,buffer,size,out size,IntPtr.Zero)) continue;
                    string path=Marshal.PtrToStringUni(IntPtr.Add(buffer,4));
                    if(!path.ToLowerInvariant().Contains("vid_1532&pid_02c6")) continue;
                    var h=CreateFile(path,0,3,IntPtr.Zero,3,0,IntPtr.Zero);
                    if(h.IsInvalid) { h.Dispose(); continue; }
                    IntPtr prep;
                    if(!HidD_GetPreparsedData(h,out prep)) { h.Dispose(); continue; }
                    Caps caps; int status=HidP_GetCaps(prep,out caps); HidD_FreePreparsedData(prep);
                    if(status!=0x110000 || caps.FeatureLength!=91) { h.Dispose(); continue; }
                    var device=new RazerHid {handle=h,Path=path};
                    try { device.Mode(1); return device; }
                    catch(Exception e) { errors.Add(e.Message); device.Dispose(); }
                } finally { Marshal.FreeHGlobal(buffer); }
            }
        } finally { SetupDiDestroyDeviceInfoList(set); }
        throw new InvalidOperationException("无法连接风扇接口。请退出 Synapse 后重试。 "+string.Join("; ",errors));
    }
    internal static byte[] Packet(byte id, ushort command, byte[] args) {
        if(args.Length>80) throw new ArgumentException("Packet too long");
        byte[] b=new byte[91]; b[2]=id; b[6]=(byte)args.Length; b[7]=(byte)(command>>8); b[8]=(byte)command;
        Array.Copy(args,0,b,9,args.Length);
        b[89]=Checksum(b);
        return b;
    }
    internal static byte Checksum(byte[] b) {byte crc=0;for(int i=3;i<89;i++)crc^=b[i];return crc;}
    internal static void Validate(byte[] request, byte[] response) {
        if(response.Length!=91 || response[0]!=0 || response[2]!=request[2] || response[7]!=request[7] || response[8]!=request[8] || response[3]!=0 || response[4]!=0 || response[5]!=0 || response[6]!=request[6] || response[89]!=Checksum(response) || response[9]!=request[9] || response[10]!=request[10])
            throw new InvalidOperationException("HID 响应不匹配，可能有其他控制软件正在访问设备。");
        if(response[1]!=2) throw new InvalidOperationException("固件未接受命令，状态 0x"+response[1].ToString("X2"));
    }
    byte[] Send(ushort command, params byte[] args) {
        byte[] request=Packet(++sequence,command,args);
        if(!HidD_SetFeature(handle,request,request.Length)) throw new Win32Exception(Marshal.GetLastWin32Error(),"HID 请求失败");
        Thread.Sleep(5);
        var response=new byte[91];
        if(!HidD_GetFeature(handle,response,response.Length)) throw new Win32Exception(Marshal.GetLastWin32Error(),"HID 读取失败");
        Validate(request,response);
        var result=new byte[response[6]]; Array.Copy(response,9,result,0,result.Length); return result;
    }
    byte[] ReadZone(ushort command, byte zone, int length) {
        if(zone!=1 && zone!=2) throw new ArgumentOutOfRangeException("zone");
        byte[] args=new byte[length]; args[0]=1; args[1]=zone;
        byte[] result=null;
        // Only retry getters. A failed write has an ambiguous outcome and must roll back.
        for(int attempt=0;attempt<3;attempt++) {
            try {result=Send(command,args);break;}
            catch(InvalidOperationException) {if(attempt==2)throw;Thread.Sleep(20);}
        }
        if(result.Length<length || result[1]!=zone) throw new InvalidOperationException("无效风扇区域响应。");
        return result;
    }
    void Write(ushort command, params byte[] args) {
        var response=Send(command,args);
        if(response.Length<args.Length) throw new InvalidOperationException("写入响应过短。");
        for(int i=0;i<args.Length;i++) if(response[i]!=args[i]) throw new InvalidOperationException("固件返回值与请求不一致。");
    }
    public byte[] Mode(byte zone) {
        var r=ReadZone(0x0d82,zone,4);
        if(Array.IndexOf(new byte[]{0,2,3,4,5,6,7},r[2])<0 || r[3]>1) throw new InvalidOperationException("未知性能/风扇模式。");
        return new byte[]{r[2],r[3]};
    }
    public int Rpm(byte zone, bool actual) { return ReadZone(actual?(ushort)0x0d88:(ushort)0x0d81,zone,3)[2]*100; }
    public void SetMode(byte zone, byte performance, byte fan) {
        if((zone!=1 && zone!=2) || fan>1 || Array.IndexOf(new byte[]{0,2,3,4,5,6,7},performance)<0) throw new ArgumentOutOfRangeException("mode");
        Write(0x0d02,1,zone,performance,fan);
        var result=Mode(zone);
        if(result[0]!=performance || result[1]!=fan) throw new InvalidOperationException("模式回读不一致。");
    }
    public void SetRpm(byte zone, int rpm) {
        if((zone!=1 && zone!=2) || rpm<0 || rpm>5500 || rpm%100!=0) throw new ArgumentOutOfRangeException("rpm");
        Write(0x0d01,1,zone,(byte)(rpm/100));
        if(Rpm(zone,false)!=rpm) throw new InvalidOperationException("目标转速回读不一致。");
    }
    public void Auto() {
        var errors=new List<string>();
        for(byte z=1;z<=2;z++) try { var mode=Mode(z); SetMode(z,mode[0],0); } catch(Exception e) { errors.Add(e.Message); }
        if(errors.Count>0) throw new InvalidOperationException(string.Join("; ",errors));
    }
    public void Dispose() { if(handle!=null) handle.Dispose(); }
}
}
