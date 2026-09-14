using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using BladeControl;
class FakeInputs : IInputNodes {
    internal Dictionary<string,bool> States=new Dictionary<string,bool>{{"mouse",false},{"keyboard",false},{"already-disabled",true}};
    internal bool FailDisable,FailEnable,Missing;
    internal Action BeforeDisable;
    public InputNode[] External() {return new[]{new InputNode {Id="mouse"},new InputNode {Id="keyboard"},new InputNode {Id="already-disabled"}};}
    public bool Disabled(string id) {if(Missing)throw new InputNodeMissing(id);return States[id];}
    public void Disable(string id) {if(BeforeDisable!=null)BeforeDisable();States[id]=true;if(FailDisable)throw new Exception("partial disable");}
    public void Enable(string id) {if(FailEnable)throw new Exception("restore failed");States[id]=false;}
}
class InputGuardTests {
    class FakeRadio : IRadioControl {
        internal string Value="On";
        internal bool TimeoutOff,PendingOff,FailOn;
        internal int Writes;
        internal Action BeforeSet;
        public string State(int timeout){if(PendingOff){Value="Off";PendingOff=false;}return Value;}
        public void Set(string state,int timeout){if(BeforeSet!=null)BeforeSet();Writes++;if(state=="On" && FailOn)throw new Exception("restore failed");if(state=="Off" && TimeoutOff){PendingOff=true;throw new TimeoutException("late off");}Value=state;}
    }
    static void RadioTests(string root) {
        string file=Path.Combine(root,Guid.NewGuid()+"-radio.txt");
        var radio=new FakeRadio();var guard=new BluetoothSleepGuard(radio,file);
        radio.BeforeSet=delegate{Check(File.ReadAllText(file)=="1","Bluetooth recovery persisted before switching off");};
        Check(guard.Protect("test") && radio.Value=="Off","direct radio protect confirms off before returning");
        radio.BeforeSet=null;int writes=radio.Writes;guard.Protect("repeat");Check(radio.Writes==writes,"repeat power notifications do not repeat radio writes");
        guard.Restore(delegate{return false;});Check(radio.Value=="Off" && File.ReadAllText(file)=="1","stale restore cannot enable radio while sleep is requested");
        guard.Restore(delegate{return true;});Check(radio.Value=="On" && File.ReadAllText(file)=="0","verified restore clears ownership");
        radio.Value="Off";writes=radio.Writes;guard.Protect("pre-disabled");guard.Restore(delegate{return true;});Check(radio.Value=="Off" && radio.Writes==writes,"preexisting off is never enabled by recovery");
        radio.Value="On";radio.TimeoutOff=true;Check(!guard.Protect("timeout") && File.ReadAllText(file)=="1","timed-out off retains recovery intent");
        radio.TimeoutOff=false;guard.Restore(delegate{return true;});Check(!radio.PendingOff && radio.Value=="On","late off finishes before restoring on");
        guard.Protect("restore-failure");radio.FailOn=true;Check(Throws(delegate{guard.Restore(delegate{return true;});}) && File.ReadAllText(file)=="1","failed on verification keeps durable ownership");
        radio.FailOn=false;new BluetoothSleepGuard(radio,file).Restore(delegate{return true;});Check(radio.Value=="On","restart restores an interrupted radio transition");
        guard=new BluetoothSleepGuard(radio,file);
        using(var input=new ExternalInputGuard(new InputRecovery(new FakeInputs(),Path.Combine(root,Guid.NewGuid()+".json")),false,guard)) {
            input.HandleSuspendNotification(4);Check(radio.Value=="Off","suspend callback completes direct off without a UI message pump");
            input.DisplayChanged(1);input.Settled.Wait();Check(radio.Value=="Off","display-on cannot override pending suspend");
            input.HandleSuspendNotification(18);input.DisplayChanged(1);input.Settled.Wait();Check(radio.Value=="On","resume plus visible display restores radio");
            input.HandleSuspendNotification(4);input.StopAsync().Wait();Check(radio.Value=="On","exit restores radio even after suspend callback");
            writes=radio.Writes;input.HandleSuspendNotification(4);Check(radio.Writes==writes,"late callbacks after stopping cannot turn radio off");
        }
    }
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr w,IntPtr l);
    static void SendDisplay(IntPtr window,int state) {
        IntPtr data=Marshal.AllocHGlobal(24);
        try {
            Marshal.Copy(new Guid("6fe69556-704a-47a0-8f24-c28d936fda47").ToByteArray(),0,data,16);
            Marshal.WriteInt32(data,16,4);Marshal.WriteInt32(data,20,state);
            SendMessage(window,0x218,new IntPtr(0x8013),data);
        }finally {Marshal.FreeHGlobal(data);}
    }
    static int count;
    static void Check(bool value,string name){if(!value)throw new Exception(name);Console.WriteLine("PASS "+name);count++;}
    static bool Throws(Action action){try{action();return false;}catch{return true;}}
    [STAThread] static int Main(string[] args) {
        try {
            string folder=Path.GetFullPath(args[0]);Directory.CreateDirectory(folder);Controller.LogPath=Path.Combine(folder,"events.log");
            RadioTests(folder);
            string file=Path.Combine(folder,Guid.NewGuid().ToString()+".json");
            var devices=new FakeInputs();var recovery=new InputRecovery(devices,file);
            devices.BeforeDisable=delegate {Check(File.ReadAllText(file).Contains("mouse"),"recovery intent persisted before input disable");};
            recovery.Protect();devices.BeforeDisable=null;
            Check(devices.States["mouse"] && devices.States["keyboard"],"both external input classes disabled");
            recovery.Restore();Check(!devices.States["mouse"] && !devices.States["keyboard"] && devices.States["already-disabled"],"restoration never enables externally disabled devices");
            devices.FailDisable=true;Check(Throws(recovery.Protect) && !devices.States["mouse"] && !recovery.Pending,"ambiguous disable rolls back immediately");
            devices.FailDisable=false;recovery.Protect();devices.FailEnable=true;
            Check(Throws(recovery.Restore) && recovery.Pending && File.ReadAllText(file).Contains("mouse"),"failed restore retains durable ownership");
            devices.FailEnable=false;var restarted=new InputRecovery(devices,file);restarted.Restore();
            Check(!restarted.Pending && !devices.States["mouse"],"restart restores interrupted input changes");
            recovery=new InputRecovery(devices,file);recovery.Protect();devices.Missing=true;recovery.Restore();
            Check(recovery.Pending,"unplugged input retains deferred restoration record");devices.Missing=false;recovery.Restore();
            Check(!recovery.Pending && !devices.States["mouse"],"reconnection restoration completes");
            using(var guard=new ExternalInputGuard(new InputRecovery(devices,file))) {
                guard.DisplayChanged(2);guard.Settled.Wait();Check(!devices.States["mouse"],"dimmed display leaves input active");
                guard.DisplayChanged(0);guard.Settled.Wait();Check(devices.States["mouse"],"display off protects external inputs");
                guard.DisplayChanged(1);guard.Settled.Wait();Check(!devices.States["mouse"],"display on restores external inputs");
                guard.LidChanged(0);guard.Settled.Wait();Check(devices.States["mouse"],"lid close protects before display notification");
                guard.DisplayChanged(1);guard.Settled.Wait();Check(devices.States["mouse"],"display-on event cannot restore Bluetooth while lid remains closed");
                guard.DisplayChanged(0);guard.LidChanged(1);guard.Settled.Wait();Check(devices.States["mouse"],"lid open waits for display on before restoring input");
                guard.DisplayChanged(1);guard.Settled.Wait();Check(!devices.States["mouse"],"open lid and lit display restore input");
                using(var entered=new ManualResetEvent(false))using(var release=new ManualResetEvent(false)) {
                    devices.BeforeDisable=delegate {entered.Set();if(!release.WaitOne(5000))throw new Exception("test timeout");};
                    guard.DisplayChanged(0);Check(entered.WaitOne(3000),"off transition is running");guard.DisplayChanged(1);release.Set();guard.Settled.Wait();devices.BeforeDisable=null;
                    Check(!devices.States["mouse"] && !devices.States["keyboard"],"rapid on during off cannot leave inputs disabled");
                }
                for(int i=0;i<100;i++){guard.DisplayChanged(0);guard.DisplayChanged(1);}
                guard.Settled.Wait();Check(!devices.States["mouse"],"bursts settle to the latest display state");
                guard.DisplayChanged(0);guard.StopAsync().Wait();
                Check(!devices.States["mouse"],"exit waits for input restoration");
                guard.DisplayChanged(0);guard.Settled.Wait();Check(!devices.States["mouse"],"shutdown ignores late off notifications");
            }
            using(var form=new Form())using(var guard=new ExternalInputGuard(new InputRecovery(devices,file),true)) {
                // Consume Windows' initial real display/lid state before injecting test messages.
                for(int i=0;i<25;i++){Application.DoEvents();Thread.Sleep(10);}
                guard.Settled.Wait();
                IntPtr original=form.Handle,listener=guard.Handle;
                form.ShowInTaskbar=false;
                Check(form.Handle!=original && guard.Handle==listener && listener!=form.Handle,"tray handle recreation leaves independent power listener intact");
                SendDisplay(listener,0);guard.Settled.Wait();Check(devices.States["mouse"],"native power message reaches listener after minimizing to tray");
                form.ShowInTaskbar=true;
                SendDisplay(listener,1);guard.Settled.Wait();Check(!devices.States["mouse"],"native power message restores input after returning from tray");
                guard.StopAsync().Wait();
            }
            Console.WriteLine(count+" input guard tests passed; simulated device writes only.");return 0;
        }catch(Exception error){Console.WriteLine(error);return 1;}
    }
}
