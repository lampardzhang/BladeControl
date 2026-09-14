using System;
using System.IO;
using BladeControl;
class FakeFan : IRazerHid {
    public bool FailSecond,FailAuto,WasAuto,ResetTargetsOnMode;
    public int ModeWrites;
    byte[] modes={0,0,0}; int[] speeds={0,2000,2000};
    public byte[] Mode(byte z) {return new byte[]{2,modes[z]};}
    public int Rpm(byte z,bool actual) {return speeds[z];}
    public void SetMode(byte z,byte p,byte f) {ModeWrites++;modes[z]=f;if(ResetTargetsOnMode) {speeds[1]=speeds[2]=2000;}}
    public void SetRpm(byte z,int rpm) {if(z==2 && FailSecond) throw new Exception("injected zone 2 failure");speeds[z]=rpm;}
    public void Auto() {if(FailAuto) throw new Exception("injected restore failure");WasAuto=true;modes[1]=modes[2]=0;}
    public void Dispose() {}
}
class FakeGpuDevice : IGpuDevice {
    public bool Disabled,FailDisable,FailEnable,DisplayUnsafe,FailAfterDisable;
    public int EnableCalls,DisableCalls;
    public Action BeforeDisable;
    public string FindNvidia(){return "test-nvidia";}
    public bool IsDisabled(string id){return Disabled;}
    public void CheckIntegratedDisplay(){if(DisplayUnsafe || (Disabled && FailAfterDisable))throw new Exception("display unsafe");}
    public void Disable(string id){DisableCalls++;if(BeforeDisable!=null)BeforeDisable();Disabled=true;if(FailDisable)throw new Exception("disable ambiguous failure");}
    public void Enable(string id){EnableCalls++;if(FailEnable)throw new Exception("enable failed");Disabled=false;}
}
class Tests {
    static int count;
    static void Check(bool pass,string name) {if(!pass) throw new Exception("FAIL "+name);Console.WriteLine("PASS "+name);count++;}
    static bool Throws(Action a) {try {a();return false;} catch {return true;}}
    static void OfficeFanTests(string root) {
        using(var c=new Controller(Path.Combine(root,Guid.NewGuid().ToString("N")))) {
            var fan=new FakeFan();c.FanDevice=fan;
            double cpu=47,gpu=40;int gpuReads=0;long clock=0;
            c.ReadPolicyCpu=delegate {return cpu;};c.ReadPolicyGpu=delegate {gpuReads++;return gpu;};c.TrialClock=delegate{return clock;};
            c.StartOfficeFan();c.CheckFanPolicy();
            Check(c.FanPolicyActive && !c.State.Fan && gpuReads==0,"office cold auto allows firmware stop without waking GPU");
            cpu=49.99;c.CheckFanPolicy();Check(!c.RequestedFanRpm.HasValue,"office remains automatic below 50 C");
            cpu=50;c.CheckFanPolicy();Check(c.RequestedFanRpm==2000 && fan.Rpm(2,false)==2000,"office exactly 50 C sets both fans to 2000");
            int writes=fan.ModeWrites;c.CheckFanPolicy();Check(fan.ModeWrites==writes,"stable policy does not rewrite fan modes");
            cpu=48;clock+=40*System.Diagnostics.Stopwatch.Frequency;c.CheckFanPolicy();Check(c.RequestedFanRpm==2000,"exactly 48 C does not start cool release");
            cpu=47;c.CheckFanPolicy();clock+=29*System.Diagnostics.Stopwatch.Frequency;c.CheckFanPolicy();Check(c.RequestedFanRpm==2000,"cooling hysteresis waits 30 seconds");
            cpu=49;c.CheckFanPolicy();cpu=47;c.CheckFanPolicy();clock+=29*System.Diagnostics.Stopwatch.Frequency;c.CheckFanPolicy();Check(c.RequestedFanRpm==2000,"temperature rebound resets the cooling dwell");
            clock+=System.Diagnostics.Stopwatch.Frequency;c.CheckFanPolicy();Check(!c.State.Fan && c.FanPolicyActive,"30 continuous cool seconds restores automatic stop eligibility");
            cpu=50;c.CheckFanPolicy();cpu=47;gpu=60;c.CheckFanPolicy();clock+=40*System.Diagnostics.Stopwatch.Frequency;c.CheckFanPolicy();Check(!c.State.Fan && gpuReads==0,"office cooling releases without GPU queries");
            gpu=75;c.CheckFanPolicy();Check(!c.State.Fan && gpuReads==0,"office never reads GPU even during recurring policy ticks");
            gpu=40;c.CheckFanPolicy();clock+=30*System.Diagnostics.Stopwatch.Frequency;c.CheckFanPolicy();Check(!c.State.Fan,"cool safety recovery keeps office automatic");
            cpu=50;c.CheckFanPolicy();cpu=80;c.CheckFanPolicy();Check(!c.State.Fan && c.FanPolicyStatus.Contains("80"),"CPU thermal guard restores automatic");
            int beforeManualReads=gpuReads;
            c.ReadPolicyGpu=delegate {gpuReads++;throw new Exception("manual must not query NVIDIA");};
            cpu=47;c.StartManual2000();clock+=60*System.Diagnostics.Stopwatch.Frequency;c.CheckFanPolicy();Check(c.RequestedFanRpm==2000,"explicit manual 2000 does not auto-stop at low temperature");
            c.OfficeGpuMode=false;c.GpuId="must-not-query";
            Check(c.GpuStatus().Contains("已暂停") && c.GpuTemperatureText.Contains("不读取"),"manual telemetry skips NVIDIA even outside office GPU mode");
            cpu=80;c.CheckFanPolicy();Check(!c.State.Fan,"manual retains CPU thermal protection without GPU telemetry");
            cpu=47;c.CheckFanPolicy();clock+=29*System.Diagnostics.Stopwatch.Frequency;c.CheckFanPolicy();Check(!c.State.Fan,"manual protection waits for 30 cool seconds");
            clock+=System.Diagnostics.Stopwatch.Frequency;c.CheckFanPolicy();Check(c.RequestedFanRpm==2000 && gpuReads==beforeManualReads,"manual resumes after CPU cooldown without reading GPU");
            c.GpuId=null;
            c.SuspendFanPolicy(true);Check(!c.State.Fan && c.FanPolicyActive,"screen off releases manual fan but remembers policy");
            cpu=60;c.CheckFanPolicy();Check(!c.State.Fan,"screen-off policy cannot force manual fans");
            c.SuspendFanPolicy(false);c.CheckFanPolicy();Check(c.RequestedFanRpm==2000,"screen on resumes guarded manual setting");
            Check(gpuReads==beforeManualReads,"manual resume and recurring ticks never query GPU");
            c.ReadPolicyGpu=delegate {gpuReads++;return gpu;};
            c.ReadPolicyCpu=delegate{throw new Exception("sensor lost");};fan.FailAuto=true;
            Check(Throws(c.CheckFanPolicy) && c.State.Fan && c.FanPolicyActive,"failed safety restore retains recovery and monitoring");
            fan.FailAuto=false;c.CheckFanPolicy();Check(!c.State.Fan,"next policy tick retries failed automatic restoration");
            c.ReadPolicyCpu=delegate{return double.NaN;};c.StartManual2000();Check(!c.State.Fan,"NaN refuses guarded manual mode");
            c.ReadPolicyCpu=delegate{return 47;};c.StartManual2000();c.SetFan(2400);c.CheckFanPolicy();Check(!c.FanPolicyActive && c.RequestedFanRpm==2400,"normal fan setting cancels office and manual policy");
            c.StartOfficeFan();c.SetFan(3900);c.CheckFanPolicy();Check(!c.FanPolicyActive && c.RequestedFanRpm==3900,"gaming target is unaffected by office thresholds");
            c.StartOfficeFan();c.AutoFan();Check(!c.FanPolicyActive,"explicit automatic cancels office curve");
            c.StartOfficeFan();c.Restore();Check(!c.FanPolicyActive && !c.HasChanges,"restore cancels even an idle office policy");
        }
    }
    static int Main(string[] args) {
        try {
            OfficeFanTests(args[0]);
            var packet=RazerHid.Packet(11,0x0d01,new byte[]{1,2,35});
            Check(packet.Length==91 && packet[0]==0 && packet[2]==11 && packet[6]==3 && packet[7]==13 && packet[8]==1 && packet[10]==2 && packet[11]==35,"HID packet offsets and RPM encoding");
            Check(packet[9]==1 && packet[89]==0x2f,"profile 1 and protocol XOR checksum");
            var reply=(byte[])packet.Clone();reply[1]=2;RazerHid.Validate(packet,reply);Check(true,"matching success reply");
            reply[89]^=1;Check(Throws(delegate {RazerHid.Validate(packet,reply);}),"reject corrupt CRC");reply[89]^=1;
            reply[9]=0;reply[89]=RazerHid.Checksum(reply);Check(Throws(delegate {RazerHid.Validate(packet,reply);}),"reject competing profile even with valid CRC");reply=(byte[])packet.Clone();reply[1]=2;
            reply[6]=4;reply[89]=RazerHid.Checksum(reply);Check(Throws(delegate {RazerHid.Validate(packet,reply);}),"reject response with wrong payload length");reply=(byte[])packet.Clone();reply[1]=2;
            reply[2]++;Check(Throws(delegate {RazerHid.Validate(packet,reply);}),"reject stale response / competing controller");reply[2]--;
            reply[1]=5;Check(Throws(delegate {RazerHid.Validate(packet,reply);}),"reject unsupported firmware command");
            Check(Throws(delegate {RazerHid.Validate(packet,new byte[6]);}),"reject truncated response");
            Check(CpuTemperature.Decode(400u<<21)==50,"CPU temperature conversion");
            Check(CpuTemperature.Decode((792u<<21)|0x80000)==50,"CPU temperature range offset");
            Check(CpuTemperature.Decode((792u<<21)|0x30000)==50,"CPU temperature TJ offset");
            Check(Throws(delegate {CpuTemperature.Decode(0);}) && Throws(delegate {CpuTemperature.Decode(uint.MaxValue);}),"reject unavailable CPU readings instead of displaying zero");
            bool gpuCalled=false;int fanSet=0;
            var cpuFailure=PerformanceProfiles.Apply(PerformanceProfiles.Office,delegate(int n) {throw new Exception("CPU failure");},delegate(int n) {gpuCalled=true;},delegate {gpuCalled=true;},delegate(int n) {fanSet=n;},delegate {gpuCalled=true;});
            Check(!cpuFailure.Complete && !gpuCalled && fanSet==0,"CPU failure reports partial fan result and prevents GPU change");
            int cpuApplied=0;
            var partial=PerformanceProfiles.Apply(PerformanceProfiles.Office,delegate(int n) {cpuApplied=n;},delegate(int n) {throw new Exception("driver unsupported");},delegate {},delegate(int n) {fanSet=n;},delegate {throw new Exception("device unsupported");});
            Check(cpuApplied==1800 && fanSet==0 && !partial.Complete && partial.Message.Contains("GPU 未完成"),"unsupported GPU still reports CPU and fan outcomes");
            int cpuSet=0,gpuSet=0;
            bool normalReset=false;
            var normal=PerformanceProfiles.Apply(PerformanceProfiles.Normal,delegate(int n) {cpuSet=n;},delegate(int n) {throw new Exception("normal must not lock GPU clocks");},delegate {normalReset=true;},delegate(int n) {fanSet=n;},delegate {throw new Exception("office must not run");});
            Check(normal.Complete && cpuSet==PerformanceProfiles.Gaming.CpuMhz && PerformanceProfiles.Normal.GpuMhz==PerformanceProfiles.Gaming.GpuMhz && normalReset && fanSet==2500,"normal matches gaming CPU/GPU dynamic settings with 2500 RPM");
            bool reset=false;
            var game=PerformanceProfiles.Apply(PerformanceProfiles.Gaming,delegate(int n) {cpuSet=n;},delegate(int n) {throw new Exception("must not lock game clocks");},delegate {reset=true;},delegate(int n) {fanSet=n;},delegate {throw new Exception("office must not run");});
            Check(game.Complete && cpuSet==0 && reset && fanSet==3900,"game profile restores dynamic clocks and sets 3900 RPM");
            bool cpuCalled=false;gpuCalled=false;
            Check(Throws(delegate {PerformanceProfiles.Apply(PerformanceProfiles.Gaming,delegate(int n) {cpuCalled=true;},delegate(int n) {gpuCalled=true;},delegate {gpuCalled=true;},delegate(int n) {throw new Exception("fan failure");},delegate {gpuCalled=true;});}) && !cpuCalled && !gpuCalled,"fan failure prevents raising CPU or GPU performance");
            var order=new System.Collections.Generic.List<string>();
            PerformanceProfiles.Apply(PerformanceProfiles.Office,delegate(int n) {order.Add("cpu");},delegate(int n) {order.Add("gpu");},delegate {},delegate(int n) {order.Add("fan");},delegate {order.Add("office");});
            Check(string.Join(",",order)=="fan,cpu,office","office uses integrated-only action, never GPU clock operations");
            string stateFolder=Path.Combine(args[0],Guid.NewGuid().ToString("N"));
            using(var c=new Controller(stateFolder)) {
                var fan=new FakeFan();c.FanDevice=fan;
                c.ReadTrialTemperatures=delegate {throw new Exception("sensor unavailable");};
                Check(Throws(delegate {c.SetFan(0);}) && !c.State.Fan,"reject stop when temperature unavailable before changing state");
                Check(Throws(delegate {c.SetFan(5550);}) && !c.State.Fan,"reject out-of-range fan speed");
                c.ReadTrialTemperatures=delegate {return new double[]{50};};
                long clock=0;c.TrialClock=delegate {return clock;};
                c.SetFan(0);Check(c.LowFanTrialActive && fan.Rpm(1,false)==0 && fan.Mode(1)[1]==1,"zero is an explicit manual target and arms guard");
                c.CheckLowFanSafety();Check(c.LowFanTrialActive,"cool trial remains active");
                clock=61L*System.Diagnostics.Stopwatch.Frequency;c.CheckLowFanSafety();
                Check(!c.LowFanTrialActive && !c.State.Fan && fan.WasAuto,"monotonic 60 second deadline restores auto");
                c.SetFan(1000);c.ReadTrialTemperatures=delegate {return new double[]{70};};c.CheckLowFanSafety();
                Check(!c.LowFanTrialActive && !c.State.Fan,"CPU trial thermal limit restores auto");
                c.ReadTrialTemperatures=delegate {return new double[]{double.NaN};};
                Check(Throws(delegate {c.SetFan(0);}) && !c.State.Fan,"NaN cannot permit a stop request");
                c.ReadTrialTemperatures=delegate {return new double[]{50};};c.SetFan(1900);
                c.ReadTrialTemperatures=delegate {throw new Exception("sensor lost");};fan.FailAuto=true;
                Check(Throws(c.CheckLowFanSafety) && c.LowFanTrialActive && c.State.Fan,"failed thermal rollback retains guard and recovery intent");
                fan.FailAuto=false;c.CheckLowFanSafety();Check(!c.LowFanTrialActive && !c.State.Fan,"next safety tick retries recovery");
                c.ReadTrialTemperatures=delegate {return new double[]{50};};c.SetFan(1400);
                Check(c.LowFanTrialActive && fan.Rpm(2,false)==1400,"sub-1500 request accepted as bounded trial");c.AutoFan();
                Check(!Controller.FanCondition(0,0,100,1,70,3).Contains("停转"),"100 RPM cannot count as stopped");
                Check(Controller.FanCondition(0,0,0,1,10,3).Contains("已连续测得停转"),"zero requires actual zero samples");
                c.ApplyProfileFan(0); int beforeAuto=fan.ModeWrites; c.ApplyProfileFan(0); Check(fan.ModeWrites==beforeAuto && !c.RequestedFanRpm.HasValue,"repeated office automatic fan does not rewrite mode"); c.SetFan(1500);Check(c.State.Fan && fan.Rpm(1,false)==1500 && fan.Rpm(2,false)==1500,"1500 RPM accepted by controller for both zones");
                c.Restore();
                c.SetFan(3500);Check(c.State.Fan && fan.Rpm(1,false)==3500 && fan.Rpm(2,false)==3500,"manual speed across both zones");
                using(var reload=new Controller(stateFolder)) Check(reload.State.Fan,"durable recovery marker before hardware writes");
                c.Restore();Check(!c.HasChanges && fan.WasAuto,"restore owned fan setting");
                fan.ResetTargetsOnMode=true;c.SetFan(3500);
                Check(fan.Rpm(1,false)==3500 && fan.Rpm(2,false)==3500,"mode reset cannot overwrite previously written fan 1 target");
                int modeWrites=fan.ModeWrites;c.SetFan(3000);
                Check(fan.ModeWrites==modeWrites && fan.Rpm(1,false)==3000,"already manual changes speed without another mode reset");
                c.Restore();Check(!c.RequestedFanRpm.HasValue,"automatic control clears pending manual tracking");
                Check(Controller.FanCondition(2000,2000,2400,1,10,0).Contains("正在调整"),"slow ramp is not reported as successful convergence");
                Check(Controller.FanCondition(2000,2000,2600,1,70,0).Contains("偏离"),"late actual RPM mismatch is reported");
                Check(Controller.FanCondition(2000,3500,2000,1,5,3).Contains("目标已变化"),"target overwrite detected even when actual RPM matches");
                Check(Controller.FanCondition(2000,2000,null,1,70,0).Contains("不可用"),"missing tachometer cannot report success");
                Check(Controller.FanCondition(2000,2000,2000,0,70,3).Contains("模式已变化"),"automatic takeover detected");
                Check(Controller.FanCondition(2000,2000,2100,1,70,3).Contains("已接近目标"),"three near-target samples produce qualified confirmation");
                fan.FailSecond=true;
                Check(Throws(delegate {c.SetFan(3500);}) && !c.State.Fan && fan.Mode(1)[1]==0,"partial write failure restores automatic control");
                fan.FailAuto=true;
                Check(Throws(delegate {c.SetFan(3500);}) && c.State.Fan,"failed rollback retains recovery marker");
                using(var reload=new Controller(stateFolder)) Check(reload.State.Fan,"failed recovery survives restart");
                fan.FailAuto=false;c.Restore();
                Check(Throws(delegate {c.SetCpu(999,false);}) && c.State.OwnedPlan==null,"invalid CPU input cannot create a power plan");
                c.GpuId="GPU-1234";c.GpuMax=3090;
                Check(Throws(delegate {c.SetGpu(4000);}) && !c.State.Gpu,"GPU limit cannot exceed reported maximum");
            }
            string gpuFolder=Path.Combine(args[0],Guid.NewGuid().ToString("N"));
            using(var c=new Controller(gpuFolder)) {
                var device=new FakeGpuDevice();c.GpuDeviceControl=device;
                int resets=0;c.ResetClocksForOffice=delegate {resets++;};
                device.BeforeDisable=delegate {using(var reload=new Controller(gpuFolder))Check(reload.State.GpuDeviceDisabled && reload.State.GpuDeviceId=="test-nvidia","recovery intent saved before device disable");};
                device.DisplayUnsafe=true;
                Check(Throws(c.DisableGpuForDiagnostics) && device.DisableCalls==0 && resets==0,"active discrete display rejects office before any GPU mutation");
                device.DisplayUnsafe=false;device.Disabled=true;
                Check(Throws(c.DisableGpuForDiagnostics) && !c.State.GpuDeviceDisabled && device.EnableCalls==0,"externally disabled device is never claimed or enabled");
                device.Disabled=false;c.DisableGpuForDiagnostics();
                Check(device.Disabled && c.OfficeGpuMode && c.HasChanges && resets==1,"office disables discrete GPU and retains recovery ownership");
                c.GpuId="GPU-test";c.GpuError="must not invoke NVIDIA";
                Check(c.GpuStatus().Contains("已停用") && c.GpuTemperatureText.Contains("不读取"),"office refresh uses only PnP status and bypasses nvidia-smi");
                c.DetectGpu();Check(c.GpuId=="GPU-test","redetection in office never invokes NVIDIA");
                c.DisableGpuForDiagnostics();Check(resets==1 && device.DisableCalls==1,"repeated office action is idempotent");
                device.FailEnable=true;
                Check(Throws(c.RestoreGpuDevice) && c.State.GpuDeviceDisabled && c.OfficeGpuMode,"failed enable preserves durable recovery marker and suppresses telemetry");
                device.FailEnable=false;c.RestoreGpuDevice();
                Check(!device.Disabled && !c.State.GpuDeviceDisabled && !c.OfficeGpuMode,"verified enable clears recovery ownership");
                device.FailDisable=true;
                Check(Throws(c.DisableGpuForDiagnostics) && !device.Disabled && !c.State.GpuDeviceDisabled,"ambiguous disable failure rolls back device enable");
                device.FailEnable=true;
                Check(Throws(c.DisableGpuForDiagnostics) && c.State.GpuDeviceDisabled,"rollback failure retains recovery record");
                using(var reload=new Controller(gpuFolder)) {
                    reload.GpuDeviceControl=device;device.FailEnable=false;reload.RestoreGpuDevice();
                    Check(!device.Disabled && !reload.HasChanges,"restart recovers interrupted device transition");
                }
                c.State.GpuDeviceDisabled=false;c.State.GpuDeviceId=null;c.OfficeGpuMode=false;c.Save();device.FailDisable=false;device.FailAfterDisable=true;
                Check(Throws(c.DisableGpuForDiagnostics) && !device.Disabled && !c.State.GpuDeviceDisabled,"lost integrated display after disable rolls back immediately");
            }
            string autoFolder=Path.Combine(args[0],Guid.NewGuid().ToString("N"));
            using(var c=new Controller(autoFolder)) {
                var device=new FakeGpuDevice();c.GpuDeviceControl=device;
                int resets=0;c.ResetClocksForOffice=delegate {resets++;c.State.Gpu=false;c.Save();};
                c.EnterOfficeGpu();
                Check(c.OfficeGpuMode && !device.Disabled && device.DisableCalls==0 && resets==0,"office leaves NVIDIA driver enabled without querying or changing clocks");
                Check(c.GpuStatus().Contains("自动省电") && c.GpuTemperatureText.Contains("不读取"),"automatic office mode skips all NVIDIA telemetry");
                c.State.Gpu=true;c.EnterOfficeGpu();
                Check(resets==1 && !c.State.Gpu,"office releases only previously owned clock lock");
                device.Disabled=true;c.State.GpuDeviceDisabled=true;c.State.GpuDeviceId="test-nvidia";c.Save();
                c.EnterOfficeGpu();
                Check(!device.Disabled && !c.State.GpuDeviceDisabled && c.OfficeGpuMode,"office migrates a previous hard-disable into driver-managed power saving");
                device.Disabled=true;int enables=device.EnableCalls;
                Check(Throws(c.EnterOfficeGpu) && device.EnableCalls==enables,"office does not enable an externally disabled GPU");
                device.Disabled=false;device.DisplayUnsafe=true;
                Check(Throws(c.EnterOfficeGpu) && device.DisableCalls==0,"automatic office mode refuses discrete display output without disabling hardware");
            }
            string shell=Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe");
            Check(Throws(delegate {Commands.Run(shell,"-NoProfile -Command exit 7");}),"nonzero native exit code is an error");
            Check(Commands.Run(shell,"-NoProfile -Command Write-Output command-ok").Contains("command-ok"),"native command output capture");
            string plan=PowerApi.Active();Check(Guid.Parse(plan)!=Guid.Empty,"read-only native active power plan query");
            Console.WriteLine("AC="+PowerApi.Limit(plan,true)+" MHz; DC="+PowerApi.Limit(plan,false)+" MHz");
            Console.WriteLine(count+" tests passed. No hardware settings were written.");return 0;
        } catch(Exception e) {Console.WriteLine(e);return 1;}
    }
}
