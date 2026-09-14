using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BladeControl {
[DataContract] public sealed class InputNode {
    [DataMember] public string Id;
    [DataMember] public string Name;
}
internal interface IInputNodes {
    InputNode[] External();
    bool Disabled(string id);
    void Disable(string id);
    void Enable(string id);
}
internal sealed class InputNodeMissing : Exception {internal InputNodeMissing(string id):base(id) {}}
internal sealed class InputNodes : IInputNodes {
    internal const string Bluetooth="RADIO:BLUETOOTH";
    static readonly Guid KeyboardClass=new Guid("4d36e96b-e325-11ce-bfc1-08002be10318"),MouseClass=new Guid("4d36e96f-e325-11ce-bfc1-08002be10318"),HidClass=new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
    [StructLayout(LayoutKind.Sequential)] struct DeviceInfo {public uint Size;public Guid Class;public uint Node;public IntPtr Reserved;}
    [StructLayout(LayoutKind.Sequential)] struct PropertyKey {public Guid Format;public uint Id;}
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr SetupDiGetClassDevs(IntPtr guid,string enumerator,IntPtr window,uint flags);
    [DllImport("setupapi.dll",SetLastError=true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set,uint index,ref DeviceInfo data);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool SetupDiGetDeviceInstanceId(IntPtr set,ref DeviceInfo data,StringBuilder id,int size,out int required);
    [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool SetupDiGetDeviceProperty(IntPtr set,ref DeviceInfo data,ref PropertyKey key,out uint type,byte[] buffer,uint size,out uint required,uint flags);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)] static extern uint CM_Locate_DevNode(out uint node,string id,uint flags);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Get_DevNode_Status(out uint status,out uint problem,uint node,uint flags);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Disable_DevNode(uint node,uint flags);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Enable_DevNode(uint node,uint flags);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Get_Parent(out uint parent,uint node,uint flags);
    [DllImport("cfgmgr32.dll",CharSet=CharSet.Unicode)] static extern uint CM_Get_Device_ID(uint node,StringBuilder id,int length,uint flags);
    static string NodeId(uint node) {var id=new StringBuilder(512);uint rc=CM_Get_Device_ID(node,id,id.Capacity,0);if(rc!=0)throw new Exception("Cannot identify input ancestor: "+rc);return id.ToString();}
    static bool BluetoothNode(uint node) {
        for(int depth=0;depth<12;depth++) {
            if(NodeId(node).StartsWith("BTH",StringComparison.OrdinalIgnoreCase))return true;
            uint parent;if(CM_Get_Parent(out parent,node,0)!=0)break;node=parent;
        }
        return false;
    }
    static string Radio(string action) {
        string script=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,@"src\BluetoothRadio.ps1");
        return Commands.Run(Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe"),"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \""+script+"\" -Action "+action).Trim();
    }
    static uint Locate(string id) {uint node;uint rc=CM_Locate_DevNode(out node,id,0);if(rc==13)throw new InputNodeMissing(id);if(rc!=0)throw new Exception("Input device unavailable: "+id+" ("+rc+")");return node;}
    public InputNode[] External() {
        IntPtr set=SetupDiGetClassDevs(IntPtr.Zero,null,IntPtr.Zero,6);
        if(set==new IntPtr(-1))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var rows=new Dictionary<string,Tuple<InputNode,Guid,Guid,uint>>(StringComparer.OrdinalIgnoreCase);var containers=new HashSet<Guid>();
        try {
            for(uint index=0;;index++) {
                var info=new DeviceInfo {Size=(uint)Marshal.SizeOf(typeof(DeviceInfo))};
                if(!SetupDiEnumDeviceInfo(set,index,ref info)) {if(Marshal.GetLastWin32Error()==259)break;throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
                if(info.Class!=KeyboardClass && info.Class!=MouseClass && info.Class!=HidClass)continue;
                var key=new PropertyKey {Format=new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),Id=4};
                uint type,required;var local=new byte[1];
                if(!SetupDiGetDeviceProperty(set,ref info,ref key,out type,local,1,out required,0) || type!=0x11 || required!=1 || local[0]!=0)continue;
                key.Id=2;var raw=new byte[16];
                if(!SetupDiGetDeviceProperty(set,ref info,ref key,out type,raw,16,out required,0) || type!=0x0d || required!=16)continue;
                Guid container=new Guid(raw);
                if(container==Guid.Empty || container==new Guid("00000000-0000-0000-ffff-ffffffffffff"))continue;
                int needed;var id=new StringBuilder(512);
                if(!SetupDiGetDeviceInstanceId(set,ref info,id,id.Capacity,out needed))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if(BluetoothNode(info.Node))continue; // Bluetooth is handled by the radio, with user authorization.
                if(!id.ToString().StartsWith(@"HID\",StringComparison.OrdinalIgnoreCase) && !id.ToString().StartsWith(@"USB\",StringComparison.OrdinalIgnoreCase))continue;
                var node=new InputNode {Id=id.ToString(),Name=id.ToString()};
                rows.Add(node.Id,Tuple.Create(node,info.Class,container,info.Node));
                if(info.Class==KeyboardClass || info.Class==MouseClass)containers.Add(container);
            }
        }finally{SetupDiDestroyDeviceInfoList(set);}
        var selected=new Dictionary<string,InputNode>(StringComparer.OrdinalIgnoreCase);
        foreach(var row in rows.Values) {
            if(!containers.Contains(row.Item3) || !row.Item1.Id.StartsWith(@"HID\",StringComparison.OrdinalIgnoreCase))continue;
            var target=row;
            for(int depth=0;;depth++) {
                uint state,problem;uint rc=CM_Get_DevNode_Status(out state,out problem,target.Item4,0);
                if(rc!=0)throw new Exception("Cannot inspect external input status: "+rc);
                if((state&0x2000)!=0 || problem==22)break;
                uint parent;Tuple<InputNode,Guid,Guid,uint> candidate;
                if(depth>=8 || CM_Get_Parent(out parent,target.Item4,0)!=0 || !rows.TryGetValue(NodeId(parent),out candidate) || candidate.Item3!=row.Item3 || candidate.Item2!=HidClass)throw new Exception("External input cannot be suspended independently: "+row.Item1.Id);
                target=candidate;
            }
            selected[target.Item1.Id]=target.Item1;
        }
        // Bluetooth now has a preloaded, direct suspend callback and separate recovery ledger.
        var result=new List<InputNode>();
        foreach(var pair in selected) {
            uint ancestor=Locate(pair.Key);bool covered=false;
            for(int depth=0;depth<12;depth++) {uint parent;if(CM_Get_Parent(out parent,ancestor,0)!=0)break;ancestor=parent;if(selected.ContainsKey(NodeId(ancestor))){covered=true;break;}}
            if(!covered)result.Add(pair.Value);
        }
        return result.ToArray();
    }
    public bool Disabled(string id) {
        if(id==Bluetooth)return Radio("State")=="Off";
        uint state,problem;uint rc=CM_Get_DevNode_Status(out state,out problem,Locate(id),0);
        if(rc!=0 || (problem!=0 && problem!=22))throw new Exception("Input status unavailable: "+id+" ("+rc+", "+problem+")");
        return problem==22;
    }
    public void Disable(string id) {
        if(id==Bluetooth){if(Radio("Off")!="Off")throw new Exception("Bluetooth did not turn off");return;}
        // Nonpersistent disable; reboot is an additional recovery route.
        uint rc=CM_Disable_DevNode(Locate(id),0);
        if(rc!=0 || !Disabled(id))throw new Exception("Input disable failed: "+id+" ("+rc+")");
    }
    public void Enable(string id) {
        if(id==Bluetooth){if(Radio("On")!="On")throw new Exception("Bluetooth did not restore");return;}
        uint rc=CM_Enable_DevNode(Locate(id),0);
        if(rc!=0 || Disabled(id))throw new Exception("Input restore failed: "+id+" ("+rc+")");
    }
}
internal sealed class InputRecovery {
    readonly IInputNodes devices;
    readonly string path;
    readonly List<string> owned=new List<string>();
    internal bool Pending {get {return owned.Count>0;}}
    internal InputRecovery(IInputNodes devices,string path) {
        this.devices=devices;this.path=path;
        if(File.Exists(path))using(var file=File.OpenRead(path))owned.AddRange((string[])new DataContractJsonSerializer(typeof(string[])).ReadObject(file));
    }
    void Save() {
        Directory.CreateDirectory(Path.GetDirectoryName(path));string temp=path+".tmp";
        using(var file=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)) {new DataContractJsonSerializer(typeof(string[])).WriteObject(file,owned.ToArray());file.Flush(true);}
        if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
    }
    internal void Protect() {
        try {
            foreach(var device in devices.External()) {
                if(owned.Contains(device.Id) || devices.Disabled(device.Id))continue;
                owned.Add(device.Id);Save(); // Recovery intent is durable before changing a device.
                devices.Disable(device.Id);
                Controller.Log("待机输入：暂停外接设备 "+device.Name+" / "+device.Id);
            }
        } catch(Exception error) {
            try {Restore();}catch(Exception restore){throw new Exception(error.Message+"; recovery: "+restore.Message);}
            throw;
        }
    }
    internal void Restore() {
        var errors=new List<string>();
        foreach(string id in owned.ToArray()) {
            try {
                if(devices.Disabled(id))devices.Enable(id);
                owned.Remove(id);Save();Controller.Log("待机输入：恢复外接设备 "+id);
            }catch(InputNodeMissing){/* Keep the ledger; retry when reconnected or on next startup. */}
            catch(Exception error){errors.Add(error.Message);}
        }
        if(errors.Count>0)throw new Exception(string.Join("; ",errors));
    }
}
internal sealed class ExternalInputGuard : NativeWindow,IDisposable {
    static readonly Guid ConsoleDisplay=new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");
    static readonly Guid LidSwitch=new Guid("ba3e0f4d-b817-4094-a2d1-d56379e6a0f3");
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient,ref Guid guid,uint flags);
    [DllImport("user32.dll")] static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate uint PowerCallback(IntPtr context,uint type,IntPtr setting);
    [StructLayout(LayoutKind.Sequential)] struct PowerSubscription {public PowerCallback Callback;public IntPtr Context;}
    [DllImport("powrprof.dll")] static extern uint PowerRegisterSuspendResumeNotification(uint flags,ref PowerSubscription subscription,out IntPtr registration);
    [DllImport("powrprof.dll")] static extern uint PowerUnregisterSuspendResumeNotification(IntPtr registration);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,int message,IntPtr w,IntPtr l);
    readonly InputRecovery recovery;
    readonly object gate=new object();
    IntPtr registration,lidRegistration,suspendRegistration;
    PowerCallback powerCallback;
    BluetoothSleepGuard bluetooth;
    bool suspending;
    bool displayOff,lidClosed;
    Task worker=Task.FromResult(0);
    bool off,stopping,running;
    int revision;
    public string Status="睡眠唤醒：仅内置键盘和触摸板；初始化中";
    internal event Action<bool> DisplayStateChanged;
    internal ExternalInputGuard(InputRecovery recovery) {this.recovery=recovery;}
    internal ExternalInputGuard(InputRecovery recovery,bool notifications,BluetoothSleepGuard bluetooth=null):this(recovery) {this.bluetooth=bluetooth;if(notifications)StartNotifications();}
    internal Task Settled {get {lock(gate)return worker;}}
    internal void DisplayChanged(int state) {
        if(state!=0 && state!=1 && state!=2)return;
        lock(gate){if(stopping)return;}
        displayOff=state==0;Controller.Log("电源通知：屏幕状态="+state);PublishPowerState();
    }
    internal void LidChanged(int state) {
        if(state!=0 && state!=1)return;
        lock(gate){if(stopping)return;}
        lidClosed=state==0;Controller.Log("电源通知：盖子="+(lidClosed?"关闭":"打开"));PublishPowerState();
    }
    void PublishPowerState() {
        bool protect;lock(gate){if(stopping)return;protect=suspending || displayOff || lidClosed;off=protect;}
        if(protect && bluetooth!=null)bluetooth.Protect("合盖/熄屏");
        if(DisplayStateChanged!=null)DisplayStateChanged(protect);
        Schedule(protect);
    }
    internal ExternalInputGuard() {
        string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BladeControl");
        recovery=new InputRecovery(new InputNodes(),Path.Combine(folder,"input-recovery.json"));
        // Complete legacy recovery before the new radio controller owns any operation.
        recovery.Restore();
        bluetooth=new BluetoothSleepGuard(NativeBluetoothRadio.Shared.Value,Path.Combine(folder,"bluetooth-recovery.txt"));
        StartNotifications();
    }
    internal uint HandleSuspendNotification(uint type) {
        try {
            lock(gate){if(stopping)return 0;if(type==4){suspending=true;off=true;revision++;}else if(type==7 || type==18)suspending=false;else return 0;}
            Controller.Log(type==4?"挂起前回调：开始":"恢复回调：类型="+type);
            if(type==4 && bluetooth!=null)bluetooth.Protect("挂起前回调");
            // UI/fan work must not delay radio shutdown or run on this native callback thread.
            if(Handle!=IntPtr.Zero)PostMessage(Handle,0x8051,IntPtr.Zero,IntPtr.Zero);
        }catch(Exception error){Controller.Log("挂起回调失败："+error);}
        return 0;
    }
    void StartNotifications() {
        // ShowInTaskbar changes recreate MainForm's HWND. Own a hidden top-level
        // window so notification registrations survive every tray transition.
        CreateHandle(new CreateParams {Caption="BladeControl.PowerEvents",ExStyle=0x80});
        try {
            Guid guid=ConsoleDisplay;registration=RegisterPowerSettingNotification(Handle,ref guid,0);
            if(registration==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            guid=LidSwitch;lidRegistration=RegisterPowerSettingNotification(Handle,ref guid,0);
            if(lidRegistration==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            powerCallback=delegate(IntPtr context,uint type,IntPtr setting){return HandleSuspendNotification(type);};
            var subscription=new PowerSubscription {Callback=powerCallback};
            uint result=PowerRegisterSuspendResumeNotification(2,ref subscription,out suspendRegistration);
            if(result!=0)throw new System.ComponentModel.Win32Exception((int)result);
        }catch{Dispose();throw;}
        Controller.Log("电源通知：独立窗口及挂起前回调已注册，句柄="+Handle.ToInt64());
        Schedule(false); // Recover interrupted sessions before accepting a new off event.
    }
    void Schedule(bool displayOff) {
        lock(gate) {
            if(stopping && displayOff)return;
            off=displayOff;revision++;
            if(!running){running=true;worker=Task.Run((Action)Drain);}
        }
    }
    void Drain() {
        while(true) {
            int current;bool desired;
            lock(gate){current=revision;desired=off;}
            try {
                if(desired)recovery.Protect();else {
                    recovery.Restore();
                    if(bluetooth!=null)bluetooth.Restore(delegate{lock(gate)return stopping || (!off && !suspending);});
                }
                Status=desired?"外接 HID 已暂停":"外接 HID 已恢复";
                if(bluetooth!=null)Status+="；"+bluetooth.Status;
            }catch(Exception error){Status="待机输入处理失败："+error.Message;Controller.Log(Status);}
            lock(gate){if(current==revision){running=false;return;}}
        }
    }
    protected override void WndProc(ref Message message) {
        if(message.Msg==0x8051)PublishPowerState();
        else if(message.Msg==0x218 && message.WParam==new IntPtr(0x8013) && message.LParam!=IntPtr.Zero) {
            Guid setting=(Guid)Marshal.PtrToStructure(message.LParam,typeof(Guid));
            if(setting==ConsoleDisplay && Marshal.ReadInt32(message.LParam,16)==4) {
                int state=Marshal.ReadInt32(message.LParam,20);
                DisplayChanged(state);
            } else if(setting==LidSwitch && Marshal.ReadInt32(message.LParam,16)==4) {
                LidChanged(Marshal.ReadInt32(message.LParam,20));
            }
        } else if(message.Msg==0x219 && message.WParam==new IntPtr(7)) {
            lock(gate){if(!stopping)Schedule(off);}
        }
        base.WndProc(ref message);
    }
    internal async Task StopAsync() {
        Task pending;
        lock(gate){stopping=true;Schedule(false);pending=worker;}
        await pending;
        // A failed restore stays in the ledger and prevents a clean UI exit.
        recovery.Restore();
        if(bluetooth!=null)bluetooth.Restore(delegate{return true;});
    }
    public void Dispose() {
        if(suspendRegistration!=IntPtr.Zero){PowerUnregisterSuspendResumeNotification(suspendRegistration);suspendRegistration=IntPtr.Zero;}
        if(registration!=IntPtr.Zero){UnregisterPowerSettingNotification(registration);registration=IntPtr.Zero;}
        if(lidRegistration!=IntPtr.Zero){UnregisterPowerSettingNotification(lidRegistration);lidRegistration=IntPtr.Zero;}
        if(Handle!=IntPtr.Zero)DestroyHandle();
    }
}
}
