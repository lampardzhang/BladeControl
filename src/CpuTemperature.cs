// SPDX-License-Identifier: MPL-2.0
// Adapted from LibreHardwareMonitor PawnIo/PawnIo.cs and Hardware/Cpu/Amd17Cpu.cs.
// Source commit 00921e66dd5a930f00a65de33376f486be29cd0b; see third-party notices.
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace BladeControl {
public sealed class CpuTemperature : IDisposable {
    SafeFileHandle handle;
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]
    static extern bool DeviceIoControl(SafeFileHandle device,uint code,byte[] input,uint inSize,byte[] output,uint outSize,out uint returned,IntPtr overlapped);
    const uint LoadModule=0xa1b22084,Execute=0xa1b22104;
    public static double Decode(uint raw) {
        if(raw==0 || raw==uint.MaxValue) throw new InvalidOperationException("CPU 温度传感器未返回有效数据");
        double degrees=(raw>>21)*0.125;
        if((raw&0x80000)!=0 || (raw&0x30000)==0x30000) degrees-=49;
        if(degrees<=0 || degrees>125) throw new InvalidOperationException("CPU 温度读数异常");
        return degrees;
    }
    void Open() {
        if(handle!=null && !handle.IsInvalid && !handle.IsClosed) return;
        if(!RazerHid.Model.Contains("RZ09-0528")) throw new Exception("CPU 温度未适配此型号");
        using(var cpu=Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")) {
            string name=Convert.ToString(cpu.GetValue("ProcessorNameString"));
            if(!name.Contains("Ryzen AI 9 365")) throw new Exception("CPU 温度未适配此处理器");
        }
        var candidate=CreateFile(@"\\?\GLOBALROOT\Device\PawnIO",0xc0000000,3,IntPtr.Zero,3,0,IntPtr.Zero);
        if(candidate.IsInvalid) {candidate.Dispose();throw new Exception("未启用（请点击启用 CPU 温度）");}
        try {
            byte[] module=File.ReadAllBytes(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,@"sensors\AMDFamily17.bin"));
            uint returned;
            if(!DeviceIoControl(candidate,LoadModule,module,(uint)module.Length,null,0,out returned,IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(),"CPU 传感器模块加载失败");
            handle=candidate;
        } catch {candidate.Dispose();throw;}
    }
    public double ReadDegrees() {
            Open();
            using(var mutex=new Mutex(false,@"Global\Access_PCI")) {
                bool acquired=false;
                try {
                    try {acquired=mutex.WaitOne(500);} catch(AbandonedMutexException) {acquired=true;}
                    if(!acquired) throw new Exception("CPU 传感器正忙");
                    var input=new byte[40];Array.Copy(Encoding.ASCII.GetBytes("ioctl_read_smn"),input,14);
                    Array.Copy(BitConverter.GetBytes((long)0x59800),0,input,32,8);
                    var output=new byte[8];uint returned;
                    if(!DeviceIoControl(handle,Execute,input,40,output,8,out returned,IntPtr.Zero) || returned!=8) throw new Win32Exception(Marshal.GetLastWin32Error(),"CPU 温度读取失败");
                    return Decode((uint)BitConverter.ToInt64(output,0));
                } finally {if(acquired) mutex.ReleaseMutex();}
            }
    }
    public string ReadText() {
        try {return ReadDegrees().ToString("0.0")+" °C";}
        catch(Exception e) {Dispose();return e.Message;}
    }
    public void Dispose() {if(handle!=null) {handle.Dispose();handle=null;}}
}
}
