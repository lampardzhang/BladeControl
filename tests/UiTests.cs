using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BladeControl;
class UiFan : IRazerHid {
 public bool Overlap;int active;byte mode;int rpm=2000;
 void Enter(){if(Interlocked.Increment(ref active)>1)Overlap=true;Thread.Sleep(35);}
 void Leave(){Interlocked.Decrement(ref active);}
 public byte[] Mode(byte z){Enter();try{return new byte[]{2,mode};}finally{Leave();}}
 public int Rpm(byte z,bool actual){Enter();try{return rpm;}finally{Leave();}}
 public void SetMode(byte z,byte p,byte f){Enter();try{mode=f;}finally{Leave();}}
 public void SetRpm(byte z,int value){Enter();try{rpm=value;}finally{Leave();}}
 public void Auto(){Enter();try{mode=0;}finally{Leave();}}
 public void Dispose(){}
}
class UiTests {
 static int count;static Exception failure;
 static System.Collections.Generic.IEnumerable<Control> All(Control c){foreach(Control child in c.Controls){yield return child;foreach(var nested in All(child))yield return nested;}}
 static void Check(bool ok,string text){if(!ok)throw new Exception(text);Console.WriteLine("PASS "+text);count++;}
 [STAThread] static int Main(string[] args){
  Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  string folder=Path.Combine(Path.GetFullPath(args[0]),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);Controller.LogPath=Path.Combine(folder,"ui-events.log");
  using(var c=new Controller(folder))using(var f=new MainForm(c,true)){
   var fake=new UiFan();c.FanDevice=fake;c.ReadTrialTemperatures=delegate {return new double[]{50};};
   c.ReadPolicyCpu=delegate{return 47;};c.ReadPolicyGpu=delegate{return 40;};
   f.StartPosition=FormStartPosition.Manual;f.Location=new Point(-10000,-10000);
   var tray=(NotifyIcon)typeof(MainForm).GetField("tray",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f);
   f.Shown+=delegate{f.BeginInvoke(new Action(async delegate{
    try{
     foreach(string name in new[]{"办公","普通","游戏"})Check(All(f).OfType<Button>().Count(x=>x.Text==name)==1,"single unified "+name+" button");
     var low=All(f).OfType<NumericUpDown>().Single();
     Check(low.Name=="LowFanRpm" && low.Minimum==0 && low.Maximum==1900 && low.Increment==100,"low fan trial covers 0 to 1900 RPM in 100 RPM steps");
     using(var bitmap=new Bitmap(f.Width,f.Height)) {f.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(Path.GetFullPath(args[0]),"preview.png"));}
     Check(typeof(Program).Assembly.GetManifestResourceStream("BladeControl.App.ico")!=null && Object.ReferenceEquals(f.Icon,tray.Icon),"custom icon embedded and shared by window and tray");
     var office=All(f).OfType<Button>().Single(x=>x.Text=="办公");int toggles=0,layouts=0;Rectangle bounds=office.Bounds;
     office.EnabledChanged+=delegate{toggles++;};f.Controls[0].Layout+=delegate{layouts++;};
     Task read=f.RefreshAsync();Check(office.Enabled,"buttons remain enabled during telemetry read");await read;
     Check(toggles==0,"polling never toggles button enable state");
     Check(layouts==0 && office.Bounds==bounds,"dynamic updates do not relayout static controls");
     var label=All(f).OfType<Label>().First(x=>!x.AutoSize);int textEvents=0;label.TextChanged+=delegate{textEvents++;};MainForm.UpdateText(label,label.Text);
     Check(textEvents==0,"unchanged value is not reassigned");
     read=f.RefreshAsync();All(f).OfType<Button>().Single(x=>x.Text=="3500").PerformClick();await read;
     var deadline=DateTime.UtcNow.AddSeconds(8);
     while((!c.RequestedFanRpm.HasValue || ((bool)typeof(MainForm).GetField("busy",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f) || (bool)typeof(MainForm).GetField("refreshing",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f))) && DateTime.UtcNow<deadline)await Task.Delay(20);
     Check(c.RequestedFanRpm==3500 && !fake.Overlap,"click during polling is queued without overlapping hardware IO");
     f.WindowState=FormWindowState.Minimized;Check(!f.Visible && !f.ShowInTaskbar && tray.Visible,"minimize retains tray icon");
     var telemetryTimer=(System.Windows.Forms.Timer)typeof(MainForm).GetField("timer",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f);
     Check(!telemetryTimer.Enabled,"tray suspends background hardware polling");
     c.SetFan(0);f.RestoreWindow();f.MinimizeToTray();
     Check(telemetryTimer.Enabled && c.LowFanTrialActive,"minimizing a stop trial keeps safety monitoring running");
     c.ReadTrialTemperatures=delegate {throw new Exception("sensor lost");};
     deadline=DateTime.UtcNow.AddSeconds(8);
     do {await f.RefreshAsync();await Task.Delay(20);} while(c.LowFanTrialActive && DateTime.UtcNow<deadline);
     Check(!c.LowFanTrialActive && !telemetryTimer.Enabled && !c.HasChanges,"hidden trial restores automatic on sensor loss and then stops polling");
     c.ReadTrialTemperatures=delegate {return new double[]{50};};
     foreach(var preset in new[]{PerformanceProfiles.Office,PerformanceProfiles.Normal,PerformanceProfiles.Gaming}) {
      Check(tray.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Any(x=>Object.ReferenceEquals(x.Tag,preset) && x.Text.Contains(preset.FanLabel)),"tray contains "+preset.Name+" with linked fan target");
      PerformancePreset selected=null;
      using(var item=MainForm.CreateProfileMenuItem(preset,delegate(PerformancePreset chosen) {selected=chosen;c.ApplyProfileFan(chosen.FanRpm);return Task.FromResult(0);})) item.PerformClick();
      Check(Object.ReferenceEquals(selected,preset) && (preset.FanRpm==0?!c.RequestedFanRpm.HasValue:c.RequestedFanRpm==preset.FanRpm) && !f.Visible,"tray callback applies "+preset.Name+" fan target while window stays hidden");
     }
     c.StartOfficeFan();await f.RefreshAsync();
     Check(telemetryTimer.Enabled && !f.Visible,"office policy keeps monitoring while in tray");
     var manual=tray.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(x=>x.Name=="Manual2000");
     Check(manual.Text=="手动 2000 RPM","tray provides explicit manual 2000 item");
     manual.PerformClick();deadline=DateTime.UtcNow.AddSeconds(8);
     do {await Task.Delay(20);}while((c.RequestedFanRpm!=2000 || ((bool)typeof(MainForm).GetField("busy",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f) || (bool)typeof(MainForm).GetField("refreshing",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(f))) && DateTime.UtcNow<deadline);
     Check(c.RequestedFanRpm==2000 && !f.Visible && telemetryTimer.Enabled,"tray manual applies without opening window and keeps thermal monitor");
     await f.HandleDisplayStateAsync(true);
     Check(!c.State.Fan && !telemetryTimer.Enabled,"display off releases manual cooling and stops timer");
     await f.RefreshAsync();Check(!c.State.Fan,"refresh cannot reapply manual fans while screen is off");
     await f.HandleDisplayStateAsync(false);
     Check(c.RequestedFanRpm==2000 && telemetryTimer.Enabled && !fake.Overlap,"display on resumes manual policy without overlapping IO");
     f.RestoreWindow();Check(telemetryTimer.Enabled,"opening window resumes telemetry");Check(f.Visible && f.ShowInTaskbar && f.WindowState==FormWindowState.Normal,"tray restores window");
     f.Close();
    }catch(Exception e){failure=e;f.Dispose();Application.ExitThread();}
   }));};
   Application.Run(f);
   if(failure!=null){Console.WriteLine("FAIL "+failure.Message);return 1;}
   Check(f.IsDisposed && !tray.Visible && !c.HasChanges,"exit restores owned settings and disposes tray");
  }
  Console.WriteLine(count+" UI tests passed. Fan writes used a simulated device.");return 0;
 }
}
