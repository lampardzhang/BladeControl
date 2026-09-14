using System;
using System.IO;
using System.Threading;
using BladeControl;
class HardwareTrial {
    static string report;
    static void Note(string text) {File.AppendAllText(report,DateTime.Now.ToString("s")+" "+text+Environment.NewLine);}
    static int Main(string[] args) {
        report=Path.GetFullPath(args[0]);
        using(var c=new Controller(Path.Combine(Path.GetDirectoryName(report),"trial-recovery"))) {
            try {
                Note("Blade Control 1.12 live HID trial; profile 1; no firmware flashing; no CPU/GPU settings changed.");
                c.ConnectFan();if(c.FanDevice==null)throw new Exception(c.FanError);
                if(c.HasChanges) {c.Restore();Note("Recovered prior interrupted trial.");}
                Note("BEFORE "+c.FanStatus().Replace(Environment.NewLine,"; "));
                foreach(int rpm in new int[]{1500,1000,0}) {
                    var temperatures=c.ReadTrialTemperatures();
                    Note("START requested="+rpm+" CPU="+temperatures[0]+" GPU="+temperatures[1]);
                    c.SetFan(rpm);
                    while(c.LowFanTrialActive) {
                        c.CheckLowFanSafety();
                        temperatures=c.ReadTrialTemperatures();
                        Note("SAMPLE requested="+rpm+" CPU="+temperatures[0]+" GPU="+temperatures[1]+" "+c.FanStatus().Replace(Environment.NewLine,"; "));
                        if(c.LowFanTrialActive)Thread.Sleep(4000);
                    }
                    Note("END requested="+rpm+" "+c.LowFanTrialMessage);
                    if(!c.LowFanTrialMessage.StartsWith("60 秒"))throw new Exception("Trial ended early; remaining targets skipped.");
                }
                Note("SUCCESS measurements complete; automatic mode restored.");return 0;
            } catch(Exception e) {Note("ERROR "+e);return 1;}
            finally {
                if(c.State.Fan) {
                    for(int attempt=0;attempt<3 && c.State.Fan;attempt++) try {c.AutoFan();Note("FINALLY restored automatic.");} catch(Exception e) {Note("RESTORE ERROR "+e.Message);Thread.Sleep(500);}
                }
                Note("FINAL "+c.FanStatus().Replace(Environment.NewLine,"; "));
            }
        }
    }
}
