using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace BladeControl {
public interface IGpuDevice {
    string FindNvidia();
    bool IsDisabled(string id);
    void CheckIntegratedDisplay();
    void Disable(string id);
    void Enable(string id);
}
// Uses Windows PnP, not NVML telemetry. A disabled device is not a wattmeter:
// the platform/driver still decides whether its physical power rail is removed.
public sealed class GpuDevice : IGpuDevice {
    static readonly Guid DisplayClass=new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
    [StructLayout(LayoutKind.Sequential)] struct DeviceInfo {public uint Size;public Guid Class;public uint DevInst;public IntPtr Reserved;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct DisplayDevice {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Key;
    }
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr SetupDiGetClassDevs(ref Guid guid,string enumerator,IntPtr parent,uint flags);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set,uint index,ref DeviceInfo data);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool SetupDiGetDeviceInstanceId(IntPtr set,ref DeviceInfo data,StringBuilder id,int size,out int required);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)] static extern uint CM_Locate_DevNode(out uint devInst,string id,uint flags);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Get_DevNode_Status(out uint status,out uint problem,uint devInst,uint flags);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Disable_DevNode(uint devInst,uint flags);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Enable_DevNode(uint devInst,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool EnumDisplayDevices(string device,uint index,ref DisplayDevice data,uint flags);
    public string FindNvidia() {
        Guid cls=DisplayClass;IntPtr set=SetupDiGetClassDevs(ref cls,null,IntPtr.Zero,2);
        if(set==new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            var matches=new List<string>();
            for(uint i=0;;i++) {
                var data=new DeviceInfo {Size=(uint)Marshal.SizeOf(typeof(DeviceInfo))};
                if(!SetupDiEnumDeviceInfo(set,i,ref data)) {
                    int error=Marshal.GetLastWin32Error();if(error==259)break;throw new Win32Exception(error);
                }
                var id=new StringBuilder(512);int required;
                if(!SetupDiGetDeviceInstanceId(set,ref data,id,id.Capacity,out required)) throw new Win32Exception(Marshal.GetLastWin32Error());
                // This app is specifically for the user's RZ09-0528 / RTX 5070 Ti.
                if(id.ToString().StartsWith(@"PCI\VEN_10DE&DEV_2F58&SUBSYS_300E1A58",StringComparison.OrdinalIgnoreCase)) matches.Add(id.ToString());
            }
            if(matches.Count!=1)throw new Exception("未唯一识别本机 RTX 5070 Ti，未更改显卡设备。");
            return matches[0];
        } finally {SetupDiDestroyDeviceInfoList(set);}
    }
    static uint Locate(string id) {
        uint node;uint code=CM_Locate_DevNode(out node,id,0);
        if(code!=0)throw new Exception("显卡设备不可用，Windows PnP 错误 "+code);
        return node;
    }
    public bool IsDisabled(string id) {
        uint status,problem;uint code=CM_Get_DevNode_Status(out status,out problem,Locate(id),0);
        if(code!=0)throw new Exception("读取显卡状态失败："+code);
        if(problem==22)return true;
        if(problem!=0)throw new Exception("显卡存在设备错误 "+problem+"，未继续切换。");
        return false;
    }
    public void CheckIntegratedDisplay() {
        bool amdActive=false;
        for(uint i=0;;i++) {
            var device=new DisplayDevice {Size=Marshal.SizeOf(typeof(DisplayDevice))};
            if(!EnumDisplayDevices(null,i,ref device,0))break;
            if((device.Flags&1)==0)continue;
            if(device.Id.IndexOf("VEN_10DE",StringComparison.OrdinalIgnoreCase)>=0 || device.Description.IndexOf("NVIDIA",StringComparison.OrdinalIgnoreCase)>=0)
                throw new Exception("独显正在输出画面。请先在 NVIDIA 控制面板选择 Optimus，并断开连接独显的外接显示器，再切换办公模式。");
            if(device.Id.IndexOf("VEN_1002",StringComparison.OrdinalIgnoreCase)>=0 && device.Description.IndexOf("880M",StringComparison.OrdinalIgnoreCase)>=0)amdActive=true;
        }
        if(!amdActive)throw new Exception("未确认 Radeon 880M 集显正在输出画面，保留独显以避免黑屏。");
    }
    public void Disable(string id) {
        // Nonpersistent disable: reboot remains a recovery route if the app crashes.
        uint code=CM_Disable_DevNode(Locate(id),0);
        if(code!=0)throw new Exception("停用独显失败，Windows PnP 错误 "+code+"（需要管理员权限；不会强制重启）。");
        if(!IsDisabled(id))throw new Exception("独显未进入已停用状态。");
    }
    public void Enable(string id) {
        uint code=CM_Enable_DevNode(Locate(id),0);
        if(code!=0)throw new Exception("恢复独显失败，Windows PnP 错误 "+code);
        if(IsDisabled(id))throw new Exception("独显仍处于停用状态。");
    }
}
}
