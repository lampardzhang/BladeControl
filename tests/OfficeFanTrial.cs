using System;
using System.IO;
using System.Threading;
using BladeControl;
class OfficeFanTrial {
    static string path;
    static void Log(string text){File.AppendAllText(path,DateTime.Now.ToString("s")+" "+text+Environment.NewLine);}
    static int Main(string[] args) {
        path=args[0];
        bool created;
        using(var mutex=new Mutex(true,@"Local\BladeControl.RZ090528",out created)) {
            if(!created){Log("FAIL another controller is running");return 1;}
            using(var c=new Controller()) {
                try {
                    if(c.HasChanges)throw new Exception("Previous controller recovery is incomplete");
                    c.ConnectFan();if(c.FanDevice==null)throw new Exception(c.FanError);
                    Log("BEFORE "+c.FanStatus());
                    c.StartManual2000();
                    for(int sample=0;sample<9;sample++) {
                        c.CheckFanPolicy();
                        if(c.RequestedFanRpm!=2000)throw new Exception("2000 target not held: "+c.FanPolicyStatus);
                        Log("MANUAL CPU="+c.Temperature.ReadDegrees()+" "+c.FanStatus());
                        if(sample<8)Thread.Sleep(4000);
                    }
                    for(byte zone=1;zone<=2;zone++)if(Math.Abs(c.FanDevice.Rpm(zone,true)-2000)>300)throw new Exception("Actual speed not near 2000 after settling");
                    c.SuspendFanPolicy(true);
                    if(c.FanDevice.Mode(1)[1]!=0 || c.FanDevice.Mode(2)[1]!=0)throw new Exception("Screen-off handback did not read back automatic");
                    Log("SIMULATED DISPLAY-OFF HANDLER: "+c.FanStatus());
                    c.SuspendFanPolicy(false);c.StartOfficeFan();
                    for(int sample=0;sample<9;sample++) {
                        c.CheckFanPolicy();Log("OFFICE CPU="+c.Temperature.ReadDegrees()+" "+c.FanPolicyStatus+" "+c.FanStatus());
                        if(sample<8)Thread.Sleep(4000);
                    }
                    Log("PASS manual target, actual speed, handback and office policy at natural temperature");
                    return 0;
                } catch(Exception error){Log("FAIL "+error);return 1;}
                finally {
                    try{c.AutoFan();Log("FINALLY restored automatic: "+c.FanStatus());}
                    catch(Exception error){Log("RESTORE FAILED; recovery marker retained: "+error);throw;}
                }
            }
        }
    }
}
