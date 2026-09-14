using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BladeControl {
public class CommandFailure : Exception {
    public bool TimedOut;
    public CommandFailure(string text,bool timeout=false):base(text) { TimedOut=timeout; }
}
public static class Commands {
    public static string Run(string exe,string args) {
        using(var p=new Process()) {
            p.StartInfo=new ProcessStartInfo(exe,args) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            p.Start();
            Task<string> stdout=p.StandardOutput.ReadToEndAsync(), stderr=p.StandardError.ReadToEndAsync();
            if(!p.WaitForExit(12000)) { try {p.Kill();} catch {} throw new CommandFailure("命令超时，结果未知："+System.IO.Path.GetFileName(exe),true); }
            string result=(stdout.Result+"\r\n"+stderr.Result).Trim();
            if(p.ExitCode!=0) throw new CommandFailure(result+" [退出码 "+p.ExitCode+"]");
            return result;
        }
    }
    public static string Power(string args) { return Run(System.IO.Path.Combine(Environment.SystemDirectory,"powercfg.exe"),args); }
    public static string Nvidia(string args) { return Run(System.IO.Path.Combine(Environment.SystemDirectory,"nvidia-smi.exe"),args); }
}
[DataContract] public class Recovery {
    [DataMember] public string OriginalPlan;
    [DataMember] public string OwnedPlan;
    [DataMember] public bool Fan;
    [DataMember] public bool Gpu;
    [DataMember] public string GpuId;
    [DataMember] public bool GpuDeviceDisabled;
    [DataMember] public string GpuDeviceId;
}
public static class PowerApi {
    public static Guid Sub=new Guid("54533251-82be-4824-96c1-47b60b740d00"), Freq=new Guid("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr key,out IntPtr guid);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr key,ref Guid scheme,ref Guid sub,ref Guid setting,out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr key,ref Guid scheme,ref Guid sub,ref Guid setting,out uint value);
    public static string Active() {
        IntPtr p; uint code=PowerGetActiveScheme(IntPtr.Zero,out p);
        if(code!=0) throw new Exception("读取电源计划失败："+code);
        try {return ((Guid)Marshal.PtrToStructure(p,typeof(Guid))).ToString();} finally {LocalFree(p);}
    }
    public static uint Limit(string plan,bool ac) {
        Guid id=new Guid(plan); uint value;
        uint code=ac?PowerReadACValueIndex(IntPtr.Zero,ref id,ref Sub,ref Freq,out value):PowerReadDCValueIndex(IntPtr.Zero,ref id,ref Sub,ref Freq,out value);
        if(code!=0) throw new Exception("读取 CPU 上限失败："+code);
        return value;
    }
}
public sealed class Controller : IDisposable {
    public IRazerHid FanDevice;
    internal IGpuDevice GpuDeviceControl=new GpuDevice();
    internal Action ResetClocksForOffice {get;set;}
    public bool OfficeGpuMode;
    public CpuTemperature Temperature=new CpuTemperature();
    public string GpuTemperatureText="未读取";
    public Recovery State;
    public string FanError="未连接", GpuId, GpuName="未检测", GpuError;
    public int GpuMax;
    public string ProfileMessage="尚未应用统一档位";
    public bool ProfileComplete;
    // All policy entry points run under MainForm's hardware gate.
    int fanPolicy; // 0: direct controls, 1: office, 2: manual 2000 with thermal protection
    bool policyWrite,policySuspended,policySafety;
    long? policyCoolSince;
    
    public bool FanPolicyActive {get {return fanPolicy!=0;}}
    public string FanPolicyStatus="";
    internal Func<double> ReadPolicyCpu,ReadPolicyGpu;
    void PolicyStatus(string message) {if(FanPolicyStatus!=message){FanPolicyStatus=message;Trace(message);}}
    void PolicyWrite(Action action) {policyWrite=true;try {action();}finally {policyWrite=false;}}
    void PolicyAuto() {if(State.Fan || RequestedFanRpm.HasValue)PolicyWrite(AutoFan);}
    void CancelFanPolicy() {if(!policyWrite){fanPolicy=0;policySafety=false;policyCoolSince=null;FanPolicyStatus="";}}
    public void StartOfficeFan() {
        AutoFan();fanPolicy=1;policyCoolSince=null;
        PolicyStatus("办公风扇：低温自动（允许停转），散热目标 2000 RPM");
    }
    public void StartManual2000() {
        AutoFan();fanPolicy=2;policyCoolSince=null;
        CheckFanPolicy();
    }
    public void SuspendFanPolicy(bool suspended) {
        policySuspended=suspended;
        if(suspended) {
            policyCoolSince=null;
            if(FanPolicyActive){PolicyAuto();PolicyStatus("风扇：熄屏期间由系统自动控温");}
            else if(LowFanTrialActive)AutoFan();
        }
    }
    static double ValidTemperature(double value) {
        if(double.IsNaN(value) || double.IsInfinity(value) || value<=0 || value>125)throw new Exception("温度传感器不可用");
        return value;
    }
    public void CheckFanPolicy() {
        if(!FanPolicyActive)return;
        if(policySuspended){PolicyAuto();return;}
        long now=TrialClock();
        try {
            
            double cpu=ValidTemperature(ReadPolicyCpu());
            if(cpu>=80)throw new Exception("CPU 达到 80°C 保护线");
            if(FanDevice==null)throw new Exception(FanError);
            int actual1=FanDevice.Rpm(1,true),actual2=FanDevice.Rpm(2,true);
            if(actual1<0 || actual1>10000 || actual2<0 || actual2>10000)throw new Exception("风扇测速异常");
            bool manual=RequestedFanRpm==2000;
            if(manual && (FanDevice.Mode(1)[1]!=1 || FanDevice.Mode(2)[1]!=1 || FanDevice.Rpm(1,false)!=2000 || FanDevice.Rpm(2,false)!=2000))throw new Exception("风扇模式或目标被外部改变");
            if(manual && (DateTime.UtcNow-FanRequestedAt).TotalSeconds>=45 && (actual1<1000 || actual2<1000))throw new Exception("风扇未正常起转");
            bool needsCooling=cpu>=50;
            if(fanPolicy==1 && !manual && !policySafety && !needsCooling) {
                PolicyStatus("办公风扇：低温自动控温，允许停转");return;
            }
            // Thermal policies use CPU only; never wake NVIDIA for temperature.
            bool cool=cpu<48;
            if(policySafety) {
                PolicyAuto();
                if(!cool){policyCoolSince=null;return;}
                if(!policyCoolSince.HasValue)policyCoolSince=now;
                if((now-policyCoolSince.Value)/(double)Stopwatch.Frequency<30)return;
                policySafety=false;policyCoolSince=null;
                if(fanPolicy==1){PolicyStatus("办公风扇：保护结束，低温自动控温");return;}
            }
            if(manual && fanPolicy==1) {
                if(!cool)policyCoolSince=null;
                else {
                    if(!policyCoolSince.HasValue)policyCoolSince=now;
                    if((now-policyCoolSince.Value)/(double)Stopwatch.Frequency>=30) {
                        PolicyAuto();policyCoolSince=null;
                        PolicyStatus("办公风扇：已冷却，恢复自动，允许停转");return;
                    }
                }
            }
            if(!manual) {PolicyWrite(delegate {SetFan(2000);});policyCoolSince=null;}
            PolicyStatus(fanPolicy==1?"办公风扇：散热目标 2000 RPM（高温时恢复自动）":"手动风扇：目标 2000 RPM（CPU 温度保护；不读取独显温度）");
        } catch(Exception error) {
            policySafety=true;policyCoolSince=null;
            PolicyStatus("风扇保护："+error.Message+"；恢复系统自动控温");
            // Keep the policy armed and durable recovery marker intact if restoring fails.
            PolicyAuto();
        }
    }
    internal int? RequestedFanRpm;
    internal DateTime FanRequestedAt;
    int[] nearSamples=new int[3];
    string[] previousFanCondition=new string[3];
    DateTime lastFanTrace=DateTime.MinValue;
    internal Func<double[]> ReadTrialTemperatures;
    internal Func<long> TrialClock=delegate {return Stopwatch.GetTimestamp();};
    long? trialStarted;
    public bool LowFanTrialActive {get {return trialStarted.HasValue;}}
    public string LowFanTrialMessage="低速试验：每次约 60 秒";
    public bool HasChanges {get {return State.Fan || State.Gpu || State.GpuDeviceDisabled || State.OwnedPlan!=null;}}
    static string Folder=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BladeControl");
    string StatePath;
    public static string LogPath=System.IO.Path.Combine(Folder,"events.log");
    public Controller() : this(Folder) { }
    internal Controller(string stateFolder) {
        ReadPolicyCpu=delegate {return Temperature.ReadDegrees();};
        ReadTrialTemperatures=delegate {return new double[]{Temperature.ReadDegrees()};};
        Directory.CreateDirectory(stateFolder);
        StatePath=System.IO.Path.Combine(stateFolder,"recovery.json");
        if(File.Exists(StatePath)) {
            using(var f=File.OpenRead(StatePath)) State=(Recovery)new DataContractJsonSerializer(typeof(Recovery)).ReadObject(f);
        } else State=new Recovery();
    }
    public void Save() {
        string tmp=StatePath+".tmp";
        using(var f=new FileStream(tmp,FileMode.Create,FileAccess.Write,FileShare.None)) { new DataContractJsonSerializer(typeof(Recovery)).WriteObject(f,State); f.Flush(true); }
        if(File.Exists(StatePath)) File.Replace(tmp,StatePath,null); else File.Move(tmp,StatePath);
    }
    public static void Log(string s) { try {File.AppendAllText(LogPath,DateTime.Now.ToString("s")+" "+s+Environment.NewLine);} catch {} }
    void Trace(string text) {
        try {File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(StatePath),"events.log"),DateTime.Now.ToString("s")+" "+text+Environment.NewLine);} catch { }
    }
    public void ConnectFan() {
        if(FanDevice!=null) FanDevice.Dispose(); FanDevice=null;
        try {FanDevice=RazerHid.Open(); FanError=null;} catch(Exception e) {FanError=e.Message;}
    }
    public void DetectGpu() {
        if(OfficeGpuMode || State.GpuDeviceDisabled) {GpuName="RTX 5070 Ti · 办公自动省电";return;}
        try {
            if(GpuDeviceControl.IsDisabled(GpuDeviceControl.FindNvidia())) {GpuId=null;GpuError="独显已停用";return;}
            string text=Commands.Nvidia("--query-gpu=uuid,name,clocks.max.gr --format=csv,noheader,nounits");
            foreach(string line in text.Split('\n')) {
                string[] c=line.Split(','); int max;
                if(c.Length==3 && c[1].Contains("5070 Ti Laptop") && int.TryParse(c[2].Trim(),out max) && Regex.IsMatch(c[0].Trim(),@"^GPU-[0-9a-fA-F-]+$")) {
                    GpuId=c[0].Trim(); GpuName=c[1].Trim(); GpuMax=max; GpuError=null; return;
                }
            }
            throw new Exception("未找到适配的 RTX 5070 Ti Laptop GPU。");
        } catch(Exception e) {GpuId=null;GpuError=e.Message;}
    }
    internal static string FanCondition(int requested,int reported,int? actual,byte mode,double seconds,int nearCount) {
        if(mode!=1) return "手动模式已变化";
        if(reported!=requested) return "目标已变化";
        if(!actual.HasValue) return "实际转速不可用";
        if(requested==0 && actual.Value==0) return nearCount>=3?"已连续测得停转":"测得 0 RPM，观察中";
        if(requested!=0 && Math.Abs(actual.Value-requested)<=200) return nearCount>=3?"已接近目标（±200）":"接近目标，观察中";
        return seconds<60?"正在调整，请稍候":"实际转速持续偏离目标";
    }
    public string FanStatus() {
        if(FanDevice==null) return FanError;
        var lines=new List<string>();
        double age=(DateTime.UtcNow-FanRequestedAt).TotalSeconds;
        bool trace=RequestedFanRpm.HasValue && (age<65 || (DateTime.UtcNow-lastFanTrace).TotalSeconds>=30);
        for(byte z=1;z<=2;z++) {
            var mode=FanDevice.Mode(z);
            int target=FanDevice.Rpm(z,false);
            int? actual=null;
            try {actual=FanDevice.Rpm(z,true);} catch { }
            string line="风扇 "+z+"：实际 "+(actual.HasValue?actual+" RPM":"不可用")+" / "+(mode[1]==0?"自动":"目标 "+target+" RPM");
            if(RequestedFanRpm.HasValue) {
                int desired=RequestedFanRpm.Value;
                nearSamples[z]=mode[1]==1 && target==desired && actual.HasValue && (desired==0?actual.Value==0:Math.Abs(actual.Value-desired)<=200)?nearSamples[z]+1:0;
                string condition=FanCondition(desired,target,actual,mode[1],age,nearSamples[z]);
                line+=" / "+condition;
                if(condition!=previousFanCondition[z]) {Trace("风扇状态：请求 "+desired+" RPM；"+line);previousFanCondition[z]=condition;}
            }
            lines.Add(line);
        }
        if(trace) {Trace("风扇采样："+string.Join("；",lines));lastFanTrace=DateTime.UtcNow;}
        return string.Join(Environment.NewLine,lines);
    }
    public string GpuStatus() {
        GpuTemperatureText="已关闭温度读取";
        if(fanPolicy==2) {
            GpuTemperatureText="手动 2000 RPM 不读取";
            return "手动 2000 RPM · 已暂停独显状态和温度查询";
        }
        if(OfficeGpuMode || State.GpuDeviceDisabled) {
            GpuTemperatureText="办公模式不读取";
            if(!State.GpuDeviceDisabled)return "AMD 集显输出 · 独显自动省电 · 不轮询独显";
            string id=State.GpuDeviceId;
            bool disabled=id!=null && GpuDeviceControl.IsDisabled(id);
            return disabled?"AMD 集显输出 · NVIDIA 独显已停用 · 不轮询独显":"独显状态发生变化，请重新应用办公模式；已停止独显轮询";
        }
        if(GpuId==null) return GpuError;
        string[] c=Commands.Nvidia("-i "+GpuId+" --query-gpu=clocks.gr,clocks.mem,utilization.gpu --format=csv,noheader,nounits").Split(',');
        if(c.Length!=3) throw new Exception("GPU 数据格式异常。");
        return "核心 "+c[0].Trim()+" MHz   ·   显存 "+c[1].Trim()+" MHz   ·   使用率 "+c[2].Trim()+"%";
    }
    public string CpuStatus() {
        string plan=PowerApi.Active();
        return "当前计划上限：插电 "+FormatLimit(PowerApi.Limit(plan,true))+"   /   电池 "+FormatLimit(PowerApi.Limit(plan,false));
    }
    static string FormatLimit(uint n) {return n==0?"不限频":n+" MHz";}
    void CheckTrialTemperatures() {
        double[] values=ReadTrialTemperatures();
        if(values==null || values.Length!=1) throw new Exception("CPU 温度不可用");
        foreach(double value in values) if(double.IsNaN(value) || double.IsInfinity(value) || value<=0 || value>=70) throw new Exception("低速试验要求 CPU 温度有效且低于 70°C");
    }
    public void CheckLowFanSafety() {
        if(!LowFanTrialActive) return;
        string reason=null;
        if((TrialClock()-trialStarted.Value)/(double)Stopwatch.Frequency>=60) reason="60 秒试验结束";
        else try {
            CheckTrialTemperatures();
            for(byte z=1;z<=2;z++) {
                if(!RequestedFanRpm.HasValue || FanDevice.Mode(z)[1]!=1 || FanDevice.Rpm(z,false)!=RequestedFanRpm.Value) throw new Exception("风扇模式或目标被改变");
                int actual=FanDevice.Rpm(z,true);
                if(actual<0 || actual>10000) throw new Exception("实际转速不可用");
            }
        } catch(Exception e) {reason=e.Message;}
        if(reason==null) return;
        // Keep the guard armed on restore failure so the next timer tick retries.
        LowFanTrialMessage=reason+"；正在恢复自动";
        AutoFan();LowFanTrialMessage=reason+"；已恢复自动";Trace(LowFanTrialMessage);
    }
    public void SetFan(int rpm) {
        if(FanDevice==null) throw new Exception(FanError);
        if(rpm<0 || rpm>5500 || rpm%100!=0) throw new ArgumentOutOfRangeException("rpm");
        if(rpm<2000) CheckTrialTemperatures();
        CancelFanPolicy();
        var m1=FanDevice.Mode(1); var m2=FanDevice.Mode(2);
        State.Fan=true; Save();
        if(rpm<2000)trialStarted=TrialClock();
        try {
            // Finish both mode transitions before writing either speed. A mode change
            // may reinitialize shared firmware state, including the other fan's target.
            // When already manual, changing RPM alone avoids restarting that transition.
            if(m1[1]!=1 || m2[1]!=1) {
                FanDevice.SetMode(1,m1[0],1); FanDevice.SetMode(2,m2[0],1);
                System.Threading.Thread.Sleep(100);
            }
            FanDevice.SetRpm(1,rpm); FanDevice.SetRpm(2,rpm);
            System.Threading.Thread.Sleep(150);
            // Re-read both zones after completing the transaction.
            if(FanDevice.Mode(1)[1]!=1 || FanDevice.Mode(2)[1]!=1 || FanDevice.Rpm(1,false)!=rpm || FanDevice.Rpm(2,false)!=rpm) throw new Exception("手动模式或目标转速未保持，可能被固件或其他控制软件改变。");
            RequestedFanRpm=rpm;FanRequestedAt=DateTime.UtcNow;
            trialStarted=rpm<2000?(long?)TrialClock():null;
            LowFanTrialMessage=rpm<2000?"正在试验 "+rpm+" RPM，约 60 秒后恢复自动":"低速试验：每次约 60 秒";
            nearSamples=new int[3];previousFanCondition=new string[3];lastFanTrace=DateTime.MinValue;
            Trace("风扇请求已写入："+rpm+" RPM；等待实际转速稳定");
        } catch(Exception e) {
            try {AutoFan();} catch(Exception restore) {throw new Exception(e.Message+"\r\n恢复自动失败："+restore.Message);}
            throw new Exception(e.Message+"\r\n已恢复自动风扇。");
        }
    }
    public void AutoFan() {
        CancelFanPolicy();
        if(FanDevice==null) {ConnectFan(); if(FanDevice==null) throw new Exception(FanError);}
        RequestedFanRpm=null;nearSamples=new int[3];previousFanCondition=new string[3];
        State.Fan=true; Save();
        if(FanDevice.Mode(1)[1]!=0 || FanDevice.Mode(2)[1]!=0) FanDevice.Auto(); State.Fan=false; Save();
        trialStarted=null;
        LowFanTrialMessage="已恢复自动；可开始下一次低速试验";
    }
    internal void ApplyProfileFan(int rpm) {if(rpm==0) StartOfficeFan();else SetFan(rpm);} public void ApplyProfile(PerformancePreset preset,bool battery) {
        ProfileComplete=false;
        ProfileMessage="正在切换到"+preset.Name;
        try {
            ProfileResult result=PerformanceProfiles.Apply(preset,delegate(int mhz) {
                try {SetCpu(mhz,battery,preset.IntegratedOnly?1600:0);}
                catch(Exception original) {
                    try {RestoreCpu();}
                    catch(Exception recovery) {throw new Exception(original.Message+"；CPU 恢复失败："+recovery.Message);}
                    throw;
                }
            },SetGpu,ResetGpu,ApplyProfileFan,EnterOfficeGpu);
            ProfileComplete=result.Complete;ProfileMessage=result.Message+(preset.IntegratedOnly && result.Complete?"；电池 CPU 上限 1600 MHz":"");Trace(ProfileMessage);
        } catch(Exception e) {ProfileMessage=preset.Name+"未完成："+e.Message;Trace(ProfileMessage);throw;}
    }
    public void SetCpu(int mhz,bool battery,int batteryLimit=0) {
        if(mhz!=0 && (mhz<1000 || mhz>5000)) throw new ArgumentOutOfRangeException("mhz");
        string active=PowerApi.Active();
        if(State.OwnedPlan==null) {
            State.OriginalPlan=active; State.OwnedPlan=Guid.NewGuid().ToString(); Save();
            Commands.Power("/duplicatescheme "+State.OriginalPlan+" "+State.OwnedPlan);
            Commands.Power("/changename "+State.OwnedPlan+" BladeControl");
        } else if(active!=State.OwnedPlan && active!=State.OriginalPlan) throw new Exception("电源计划已被外部更改，请先恢复本程序设置再应用。");
        string id=State.OwnedPlan;
        Commands.Power("/setacvalueindex "+id+" SUB_PROCESSOR PROCFREQMAX "+mhz);
        // Unchecked means restore the battery value inherited from the original plan.
        uint dc=batteryLimit>0?(uint)batteryLimit:battery?(uint)mhz:PowerApi.Limit(State.OriginalPlan,false);
        Commands.Power("/setdcvalueindex "+id+" SUB_PROCESSOR PROCFREQMAX "+dc);
        Commands.Power("/setactive "+id);
        if(PowerApi.Active()!=id || PowerApi.Limit(id,true)!=mhz || PowerApi.Limit(id,false)!=dc) throw new Exception("CPU 电源计划回读不一致。");
    }
    public void RestoreCpu() {
        if(State.OwnedPlan==null) return;
        string id=State.OwnedPlan;
        if(PowerApi.Active()==id) Commands.Power("/setactive "+State.OriginalPlan);
        // Avoid removing any plan except the exact GUID created by this program.
        string plans=Commands.Power("/list");
        if(plans.IndexOf(id,StringComparison.OrdinalIgnoreCase)>=0) Commands.Power("/delete "+id);
        State.OwnedPlan=null; State.OriginalPlan=null; Save();
    }
    public void SetGpu(int mhz) {
        if(State.GpuDeviceDisabled || OfficeGpuMode) {RestoreGpuDevice();DetectGpu();}
        if(GpuId==null) throw new Exception(GpuError);
        if(mhz<300 || mhz>GpuMax) throw new ArgumentOutOfRangeException("mhz");
        bool previous=State.Gpu; string previousId=State.GpuId;
        State.Gpu=true; State.GpuId=GpuId; Save();
        try {
            string result=Commands.Nvidia("-i "+GpuId+" -lgc "+mhz+","+mhz);
            Trace(result);
            if(result.IndexOf("not supported",StringComparison.OrdinalIgnoreCase)>=0) throw new CommandFailure(result);
        } catch(CommandFailure e) {
            if(!e.TimedOut) {State.Gpu=previous; State.GpuId=previousId; Save();}
            throw;
        }
    }
    public void ResetGpu() {
        if(State.GpuDeviceDisabled || OfficeGpuMode) {RestoreGpuDevice();DetectGpu();}
        string id=State.Gpu?State.GpuId:GpuId;
        if(id==null || !Regex.IsMatch(id,@"^GPU-[0-9a-fA-F-]+$")) throw new Exception("没有可恢复的 NVIDIA GPU。");
        string result=Commands.Nvidia("-i "+id+" -rgc");
        if(result.IndexOf("not supported",StringComparison.OrdinalIgnoreCase)>=0) throw new CommandFailure(result);
        State.Gpu=false; State.GpuId=null; Save();
    }
    public void EnterOfficeGpu() {
        string id=GpuDeviceControl.FindNvidia();
        GpuDeviceControl.CheckIntegratedDisplay();
        // A disabled NVIDIA device measured substantially worse on this laptop.
        // Restore driver control so Optimus can power-gate it, without NVML polling.
        if(State.GpuDeviceDisabled)RestoreGpuDevice();
        if(GpuDeviceControl.IsDisabled(id))throw new Exception("独显被其他操作停用，请先恢复设备再使用自动省电模式。");
        OfficeGpuMode=false;
        if(State.Gpu) {
            if(ResetClocksForOffice!=null)ResetClocksForOffice();else ResetGpu();
        }
        GpuDeviceControl.CheckIntegratedDisplay();
        OfficeGpuMode=true;
        Trace("办公显卡：AMD 集显输出；独显保持驱动可用，自动省电；不锁频、不轮询 NVIDIA");
    }
    // Retained for explicit diagnostic A/B tests; never used by office profiles.
    public void DisableGpuForDiagnostics() {
        string id=GpuDeviceControl.FindNvidia();
        GpuDeviceControl.CheckIntegratedDisplay();
        if(State.GpuDeviceDisabled && State.GpuDeviceId!=id)throw new Exception("存在另一设备的待恢复记录，请先恢复。");
        if(GpuDeviceControl.IsDisabled(id)) {
            if(!State.GpuDeviceDisabled)throw new Exception("独显已由其他操作停用；本程序不会接管或重新启用它。");
            OfficeGpuMode=true;return;
        }
        // Clear our old lock before disabling. Never poll NVIDIA after this step.
        if(ResetClocksForOffice!=null)ResetClocksForOffice();
        else if(State.Gpu)ResetGpu();
        State.GpuDeviceId=id;State.GpuDeviceDisabled=true;Save();
        OfficeGpuMode=true;
        try {
            GpuDeviceControl.Disable(id);
            GpuDeviceControl.CheckIntegratedDisplay();
            Trace("办公显卡：NVIDIA 已停用，集显输出；停止 NVIDIA 轮询");
        } catch(Exception original) {
            try {RestoreGpuDevice();}
            catch(Exception recovery) {throw new Exception(original.Message+"；独显恢复失败："+recovery.Message+"；恢复记录已保留");}
            throw;
        }
    }
    public void RestoreGpuDevice() {
        if(State.GpuDeviceDisabled) {
            // Keep the durable marker until a verified enable succeeds.
            GpuDeviceControl.Enable(State.GpuDeviceId);
            State.GpuDeviceDisabled=false;State.GpuDeviceId=null;Save();
            Trace("已重新启用本程序停用的 NVIDIA 独显");
        }
        OfficeGpuMode=false;
    }
    public void Restore() {
        CancelFanPolicy();
        var errors=new List<string>();
        try {RestoreGpuDevice();} catch(Exception e) {errors.Add("独显设备："+e.Message);}
        if(State.Fan) try {AutoFan();} catch(Exception e) {errors.Add("风扇："+e.Message);}
        if(State.Gpu) try {ResetGpu();} catch(Exception e) {errors.Add("GPU："+e.Message);}
        if(State.OwnedPlan!=null) try {RestoreCpu();} catch(Exception e) {errors.Add("CPU："+e.Message);}
        if(errors.Count>0) throw new Exception(string.Join("\r\n",errors));
        ProfileMessage="已恢复本程序设置";ProfileComplete=false;
    }
    public void Dispose() {if(FanDevice!=null) FanDevice.Dispose();Temperature.Dispose();}
}
}
