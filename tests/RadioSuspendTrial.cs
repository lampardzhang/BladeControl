using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BladeControl;
class RadioSuspendTrial {
    sealed class NoInputs : IInputNodes {
        public InputNode[] External(){return new InputNode[0];}
        public bool Disabled(string id){throw new Exception("No HID writes in radio trial");}
        public void Disable(string id){throw new Exception("No HID writes in radio trial");}
        public void Enable(string id){throw new Exception("No HID writes in radio trial");}
    }
    [STAThread] static int Main(string[] args) {
        string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BladeControl");
        Controller.LogPath=args[0];BluetoothSleepGuard bluetooth=null;ExternalInputGuard listener=null;
        bool created;
        using(var mutex=new Mutex(true,@"Local\BladeControl.RZ090528",out created)) {
            if(!created){Controller.Log("FAIL another Blade Control instance is running");return 1;}
            try {
                var warm=Stopwatch.StartNew();var radio=NativeBluetoothRadio.Shared.Value;
                bluetooth=new BluetoothSleepGuard(radio,Path.Combine(folder,"bluetooth-recovery.txt"));
                bluetooth.Restore(delegate{return true;});
                Controller.Log("PRELOAD "+warm.ElapsedMilliseconds+" ms; initial="+radio.State(1000));
                if(args.Length>1 && args[1]=="--restore")return 0;
                if(radio.State(1000)!="On")throw new Exception("Bluetooth initially off; refusing to enable it for a trial");
                listener=new ExternalInputGuard(new InputRecovery(new NoInputs(),Path.Combine(folder,"radio-trial-input.json")),true,bluetooth);
                for(int i=0;i<30;i++){Application.DoEvents();Thread.Sleep(10);}
                SynchronizationContext.SetSynchronizationContext(null);listener.Settled.Wait();
                var watch=Stopwatch.StartNew();
                // Exercise the same handler from a background thread. This is a simulated
                // suspend notification, not a real sleep cycle; no UI message pump is needed.
                Task.Run(delegate{listener.HandleSuspendNotification(4);}).GetAwaiter().GetResult();
                string state=radio.State(500);Controller.Log("SIMULATED CALLBACK RETURN: "+state+" after "+watch.ElapsedMilliseconds+" ms");
                if(state!="Off" || watch.ElapsedMilliseconds>=1800)throw new Exception("Off not verified within pre-suspend budget");
                Thread.Sleep(2000);
                listener.HandleSuspendNotification(18);listener.DisplayChanged(1);listener.LidChanged(1);
                listener.StopAsync().GetAwaiter().GetResult();
                if(radio.State(1000)!="On")throw new Exception("Bluetooth did not restore");
                Controller.Log("PASS radio off timing and restore; real lid-close validation still required");return 0;
            }catch(Exception error){Controller.Log("FAIL "+error);return 1;}
            finally {
                if(listener!=null){try{listener.StopAsync().GetAwaiter().GetResult();}finally{listener.Dispose();}}
                if(bluetooth!=null)bluetooth.Restore(delegate{return true;});
            }
        }
    }
}
