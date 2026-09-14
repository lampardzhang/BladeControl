using System;
using System.IO;
using System.Threading;
using BladeControl;
class InputHardwareTrial {
    static int Main(string[] args) {
        string log=args[0];
        var devices=new InputNodes();
        var recovery=new InputRecovery(devices,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"BladeControl\input-recovery.json"));
        try {
            recovery.Restore();
            foreach(var node in devices.External())File.AppendAllText(log,"EXTERNAL "+node.Name+" "+node.Id+Environment.NewLine);
            recovery.Protect();File.AppendAllText(log,"PROTECTED "+DateTime.Now.ToString("s")+Environment.NewLine);
            foreach(var node in devices.External()) {if(!devices.Disabled(node.Id))throw new Exception("Still enabled: "+node.Id);}
            Thread.Sleep(2000);
            return 0;
        }catch(Exception error){File.AppendAllText(log,"ERROR "+error+Environment.NewLine);return 1;}
        finally {
            recovery.Restore();File.AppendAllText(log,"RESTORED "+DateTime.Now.ToString("s")+Environment.NewLine);
        }
    }
}
