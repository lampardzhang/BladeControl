using System;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BladeControl {
public class MainForm : Form {
    Controller controller;
    ExternalInputGuard inputGuard;
    Label inputStatus;
    Label fanStatus,gpuStatus,cpuStatus,connection,temperatureStatus,profileStatus;
    Button enableTemperature;
    Button[] fanPresets=new Button[6];
    NumericUpDown lowFanRpm;
    Button lowFanTest;
    Label lowFanStatus;
    CheckBox battery;
    Button fanAuto,office,normal,gaming,reconnect,restore;
    Icon appIcon;
    readonly SemaphoreSlim hardwareGate=new SemaphoreSlim(1,1);
    bool refreshing;
    NotifyIcon tray;
    ContextMenuStrip trayMenu;
    ToolStripMenuItem[] trayProfiles=new ToolStripMenuItem[3];
    ToolStripItem trayRestore,trayExit;
    ToolStripMenuItem trayManual2000;
    bool screenOff;
    TextBox log;
    System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
    bool busy,closing;
    Color green=Color.FromArgb(114,235,139),muted=Color.FromArgb(161,175,190);
    public MainForm(Controller c,bool preview=false,bool startOffice=true,bool startInTray=false,bool startManual2000=false) {
        controller=c;
        Text="Blade Control 1.20 · 雷蛇 16";
        DoubleBuffered=true;
        using(var stream=typeof(Program).Assembly.GetManifestResourceStream("BladeControl.App.ico")) {if(stream!=null) appIcon=new Icon(stream);else appIcon=(Icon)SystemIcons.Application.Clone();}
        Icon=appIcon;
        AutoScaleMode=AutoScaleMode.Dpi; ClientSize=new Size(950,Math.Min(1080,Screen.PrimaryScreen.WorkingArea.Height-80)); MinimumSize=new Size(970,900);
        StartPosition=FormStartPosition.CenterScreen; BackColor=Color.FromArgb(16,21,27); ForeColor=Color.FromArgb(231,237,243);
        Font=new Font("Microsoft YaHei UI",10); AutoScroll=true;
        var root=new TableLayoutPanel {Dock=DockStyle.Top,AutoSize=true,ColumnCount=1,Padding=new Padding(24,18,24,22)};
        Controls.Add(root);
        Label title=TextLabel("BLADE CONTROL",25,green); title.Margin=new Padding(0,0,0,4); root.Controls.Add(title);
        root.Controls.Add(TextLabel("雷蛇 Blade 16 2025   /   RZ09-0528   /   Ryzen AI 9 365 + RTX 5070 Ti",10,muted));
        connection=DynamicLabel("正在读取硬件… 默认应用办公模式。",10,green,850,28); connection.Margin=new Padding(0,12,0,14); root.Controls.Add(connection);
        inputStatus=DynamicLabel("待机输入：只保留内置键盘/触摸板；熄屏时暂停外接输入及蓝牙，亮屏恢复",9,muted,850,32);root.Controls.Add(inputStatus);

        var temperatures=Row();temperatures.Margin=new Padding(0,0,0,12);root.Controls.Add(temperatures);
        temperatureStatus=DynamicLabel("CPU 温度：读取中     GPU 温度：已关闭读取",12,green,660,48);temperatures.Controls.Add(temperatureStatus);
        enableTemperature=Button("启用 CPU 温度",async delegate {await ActionAsync(InstallTemperatureDriver,"CPU 温度组件安装流程已结束，正在重新读取");});temperatures.Controls.Add(enableTemperature);

        var fan=Card(root,"01  风扇", "点击设置两只风扇的目标 RPM。实际转速会缓慢变化，通常需要观察 30–60 秒。");
        fanStatus=DynamicLabel("风扇 1：实际转速 / 目标转速 / 状态\r\n风扇 2：实际转速 / 目标转速 / 状态",11,green,835,54); fan.Controls.Add(fanStatus);
        var row=Row(); fan.Controls.Add(row);
        int[] speeds={2000,2500,3000,3500,4000,4500};
        for(int i=0;i<speeds.Length;i++) {
            int target=speeds[i];
            var preset=Button(target.ToString(),async delegate {await ActionAsync(delegate {controller.SetFan(target);},"已提交 "+target+" RPM；正在观察实际转速，请等待约 30–60 秒");});
            preset.AutoSize=false;preset.Size=new Size(80,36);
            fanPresets[i]=preset;row.Controls.Add(preset);
        }
        fanAuto=Button("恢复自动",async delegate {await ActionAsync(controller.AutoFan,"两只风扇已恢复自动");}); row.Controls.Add(fanAuto);
        row=Row();fan.Controls.Add(row);
        row.Controls.Add(TextLabel("低速试验 RPM",10,muted));
        lowFanRpm=new NumericUpDown {Name="LowFanRpm",Minimum=0,Maximum=1900,Increment=100,Value=1500,Width=92};row.Controls.Add(lowFanRpm);
        lowFanTest=Button("试验 60 秒",async delegate {
            int target=(int)lowFanRpm.Value;
            await ActionAsync(delegate {controller.SetFan(target);},"已提交 "+target+" RPM 低速试验；以实际转速为准");
            if(controller.LowFanTrialActive) timer.Start();
        });row.Controls.Add(lowFanTest);
        lowFanStatus=DynamicLabel(controller.LowFanTrialMessage,9,muted,470,28);row.Controls.Add(lowFanStatus);
        fan.Controls.Add(TextLabel("0 表示请求停转；按 100 RPM 步进。CPU 须低于 70°C，温度或测速异常时尝试恢复自动。",9,muted));
        fan.Controls.Add(TextLabel("持续偏离目标时会明确提示并记录日志。程序不会为追赶目标而反复强制覆盖固件。",9,muted));

        var modes=Card(root,"02  使用模式", "一个按钮联动 CPU、NVIDIA GPU 和两只风扇。也可在托盘右键切换模式。");
        row=Row();modes.Controls.Add(row);
        office=Button("办公",async delegate {await ApplyUnifiedProfile(PerformanceProfiles.Office);});row.Controls.Add(office);
        normal=Button("普通",async delegate {await ApplyUnifiedProfile(PerformanceProfiles.Normal);});row.Controls.Add(normal);
        gaming=Button("游戏",async delegate {await ApplyUnifiedProfile(PerformanceProfiles.Gaming);});row.Controls.Add(gaming);
        foreach(var preset in new Button[]{office,normal,gaming}) {preset.AutoSize=false;preset.Size=new Size(180,46);preset.Font=new Font("Microsoft YaHei UI",13);}
        modes.Controls.Add(TextLabel("办公：CPU ≥50°C 时 2000 RPM；低于 48°C 稳定 30 秒后自动控温，允许停转",10,muted));
        modes.Controls.Add(TextLabel("CPU ≥80°C 或 CPU 传感器异常时恢复自动散热；熄屏交回系统控温。",9,muted));
        modes.Controls.Add(TextLabel("办公：CPU 插电 1800 / 电池 1600 MHz / 集显，独显自动省电；普通/游戏：CPU、GPU 动态频率，风扇 2500 / 3900 RPM。",9,muted));
        battery=new CheckBox {Text="CPU 档位同时应用到电池供电",AutoSize=true,Margin=new Padding(0,3,0,0)};modes.Controls.Add(battery);
        profileStatus=DynamicLabel("上次操作：尚未应用统一档位",9,muted,835,44);modes.Controls.Add(profileStatus);
        cpuStatus=DynamicLabel("CPU 频率上限：读取中",10,green,835,28);modes.Controls.Add(cpuStatus);
        gpuStatus=DynamicLabel("GPU 频率：读取中",10,green,835,28);modes.Controls.Add(gpuStatus);

        row=Row(); row.Margin=new Padding(0,4,0,4); root.Controls.Add(row);
        restore=Button("恢复本程序的全部设置",async delegate {await ActionAsync(controller.Restore,"全部设置已恢复");}); row.Controls.Add(restore);
        reconnect=Button("重新检测",async delegate {await ActionAsync(delegate {controller.ConnectFan();controller.DetectGpu();},"已重新检测硬件");}); row.Controls.Add(reconnect);
        row.Controls.Add(TextLabel("最小化 → 系统托盘  ·  关闭窗口 → 恢复并退出",9,muted));
        log=new TextBox {ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical,Width=884,Height=104,BackColor=Color.FromArgb(11,15,20),ForeColor=muted,BorderStyle=BorderStyle.FixedSingle,Font=new Font("Microsoft YaHei UI",9),Margin=new Padding(0,5,0,0)}; root.Controls.Add(log);
        trayMenu=new ContextMenuStrip();
        trayMenu.Items.Add("打开主窗口",null,delegate {RestoreWindow();});
        trayMenu.Items.Add(new ToolStripSeparator());
        PerformancePreset[] presets={PerformanceProfiles.Office,PerformanceProfiles.Normal,PerformanceProfiles.Gaming};
        for(int i=0;i<presets.Length;i++) {
            trayProfiles[i]=CreateProfileMenuItem(presets[i],async delegate(PerformancePreset preset) {await ApplyUnifiedProfile(preset,true);});
            trayMenu.Items.Add(trayProfiles[i]);
        }
        trayMenu.Items.Add(new ToolStripSeparator());
        trayManual2000=new ToolStripMenuItem("手动 2000 RPM") {Name="Manual2000"};
        trayManual2000.Click+=async delegate {
            await RunActionAsync(controller.StartManual2000,delegate {return controller.FanPolicyStatus;});
            tray.ShowBalloonTip(3000,"Blade Control · 手动风扇",controller.FanPolicyStatus,ToolTipIcon.Info);
        };
        trayMenu.Items.Add(trayManual2000);
        trayRestore=trayMenu.Items.Add("恢复全部设置",null,async delegate {RestoreWindow();await ActionAsync(controller.Restore,"全部设置已恢复");});
        trayMenu.Items.Add(new ToolStripSeparator());
        trayExit=trayMenu.Items.Add("恢复并退出",null,delegate {RestoreWindow();Close();});
        tray=new NotifyIcon {Icon=appIcon,Text="Blade Control · 双击打开",ContextMenuStrip=trayMenu,Visible=!preview};
        tray.DoubleClick+=delegate {RestoreWindow();};
        Resize+=delegate {if(WindowState==FormWindowState.Minimized) MinimizeToTray();};
        Disposed+=delegate {tray.Visible=false;tray.Dispose();trayMenu.Dispose();timer.Dispose();appIcon.Dispose();};
        timer.Interval=4000;
        timer.Tick+=async delegate {if(!busy && !refreshing) await RefreshAsync();};
        Shown+=async delegate {
            if(preview) return;
            if(startOffice) {
                try {inputGuard=new ExternalInputGuard();inputGuard.DisplayStateChanged+=OnDisplayStateChanged;AddLog("已启用独立合盖/熄屏监听和内置输入保护");}
                catch(Exception error){UpdateText(inputStatus,"唤醒限制未启动："+error.Message);AddLog(inputStatus.Text);}
            }
            if(startInTray) WindowState=FormWindowState.Minimized;
            await ActionAsync(delegate {controller.ConnectFan();if(!startOffice)controller.DetectGpu();},"检测完成");
            bool ready=true;
            if(startOffice && controller.HasChanges) ready=await ActionAsync(controller.Restore,"已清理上次未恢复的设置");
            if(startOffice && ready) await ApplyUnifiedProfile(PerformanceProfiles.Office,startInTray);
            else if(controller.HasChanges) AddLog("存在待恢复设置，请先恢复本程序设置。");
            if(startManual2000 && ready) await RunActionAsync(controller.StartManual2000,delegate {return controller.FanPolicyStatus;});
            UpdateTimer();
        };
        FormClosing+=async delegate(object sender,FormClosingEventArgs e) {
            if(closing) return;
            e.Cancel=true;
            if(busy) {AddLog("正在执行命令，请稍后再关闭。");return;}
            timer.Stop();
            bool ok=await ActionAsync(delegate {controller.Restore();if(inputGuard!=null)inputGuard.StopAsync().GetAwaiter().GetResult();},"退出前恢复完成");
            if(ok) {closing=true;Close();} else {timer.Start();MessageBox.Show(this,"恢复未完成，请查看日志并重试。恢复记录已保存，下次启动仍可恢复。","未退出",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        };
        FormClosed+=delegate {if(inputGuard!=null)inputGuard.Dispose();timer.Dispose();controller.Dispose();};
    }
    void InstallTemperatureDriver() {
        string installer=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,@"drivers\PawnIO_setup.exe");
        using(var hash=System.Security.Cryptography.SHA256.Create())
        using(var stream=File.OpenRead(installer)) {
            string actual=BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","");
            if(actual!="A3A46226C5E2824F4CDD42BE0EECBABFC672C86F7889710F5AB1E6AD385B47A0") throw new Exception("CPU 温度安装包校验失败，请重新解压完整程序包。");
        }
        controller.Temperature.Dispose();
        using(var process=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installer,"-install") {UseShellExecute=true})) {
            if(process==null || !process.WaitForExit(120000)) throw new Exception("安装程序尚未完成；完成后点击重新检测。");
            if(process.ExitCode!=0) throw new Exception("CPU 温度组件未安装成功，退出码："+process.ExitCode);
        }
    }
    internal void MinimizeToTray() {
        tray.Visible=true;ShowInTaskbar=false;Hide();UpdateTimer();
        tray.Text=controller.FanPolicyActive?"Blade Control · 风扇温控中":controller.LowFanTrialActive?"Blade Control · 低速试验监测中":"Blade Control · 托盘暂停状态采样 · 双击查看";
    }
    void UpdateTimer() {
        if(!screenOff && !closing && (Visible || controller.LowFanTrialActive || controller.FanPolicyActive))timer.Start();else timer.Stop();
    }
    async void OnDisplayStateChanged(bool off) {await HandleDisplayStateAsync(off);}
    internal async Task HandleDisplayStateAsync(bool off) {
        screenOff=off;UpdateTimer();
        await hardwareGate.WaitAsync();
        try {if(!closing)await Task.Run(delegate {controller.SuspendFanPolicy(screenOff);});}
        catch(Exception error){AddLog("熄屏风扇恢复失败，亮屏后重试："+error.Message);}
        finally {hardwareGate.Release();UpdateTimer();}
        if(!screenOff)await RefreshAsync();
    }
    internal void RestoreWindow() {
        ShowInTaskbar=true;Show();WindowState=FormWindowState.Normal;Activate();
        UpdateTimer();
    }
    internal static ToolStripMenuItem CreateProfileMenuItem(PerformancePreset preset,Func<PerformancePreset,Task> apply) {
        var item=new ToolStripMenuItem(preset.Name+"  ·  "+preset.FanLabel) {Tag=preset};
        item.Click+=async delegate {await apply(preset);};
        return item;
    }
    async Task ApplyUnifiedProfile(PerformancePreset preset,bool fromTray=false) {
        bool onBattery=battery.Checked;
        await RunActionAsync(delegate {controller.ApplyProfile(preset,onBattery);},delegate {return controller.ProfileMessage;});
        UpdateText(profileStatus,"上次操作："+controller.ProfileMessage);
        profileStatus.ForeColor=controller.ProfileComplete?green:Color.FromArgb(240,193,117);
        if(fromTray) tray.ShowBalloonTip(4000,"Blade Control · "+preset.Name,controller.ProfileMessage,controller.ProfileComplete?ToolTipIcon.Info:ToolTipIcon.Warning);
    }
    Label DynamicLabel(string text,int size,Color color,int width,int height) {
        return new Label {Text=text,AutoSize=false,Size=new Size(width,height),AutoEllipsis=true,ForeColor=color,Font=new Font("Microsoft YaHei UI",size),Margin=new Padding(0,4,8,4)};
    }
    internal static void UpdateText(Label label,string text) {if(label.Text!=text) label.Text=text;}
    Label TextLabel(string text,int size,Color color) {return new Label {Text=text,AutoSize=true,ForeColor=color,Font=new Font("Microsoft YaHei UI",size),Margin=new Padding(0,4,8,4),MaximumSize=new Size(855,0)};}
    FlowLayoutPanel Row() {return new FlowLayoutPanel {AutoSize=true,WrapContents=true,FlowDirection=FlowDirection.LeftToRight,Margin=new Padding(0,3,0,3),MaximumSize=new Size(855,0)};}
    FlowLayoutPanel Card(TableLayoutPanel root,string title,string description) {
        var card=new FlowLayoutPanel {FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoSize=true,Width=884,MinimumSize=new Size(884,0),Padding=new Padding(16,9,12,11),Margin=new Padding(0,0,0,10),BackColor=Color.FromArgb(26,33,42)};
        card.Controls.Add(TextLabel(title,14,ForeColor));card.Controls.Add(TextLabel(description,9,muted));root.Controls.Add(card);return card;
    }
    Button Button(string text,EventHandler handler) {
        var b=new Button {Text=text,AutoSize=true,Height=33,FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(40,56,49),ForeColor=green,Padding=new Padding(8,3,8,3),Margin=new Padding(5,0,5,0)};b.FlatAppearance.BorderColor=Color.FromArgb(64,90,72);b.Click+=handler;return b;
    }
    void AddLog(string message) {log.AppendText(DateTime.Now.ToString("HH:mm:ss")+"  "+message+Environment.NewLine);Controller.Log(message);}
    void EnableControls() {
        fanAuto.Enabled=!busy && controller.FanDevice!=null;
        foreach(var preset in fanPresets) preset.Enabled=fanAuto.Enabled;
        lowFanTest.Enabled=lowFanRpm.Enabled=fanAuto.Enabled;
        office.Enabled=normal.Enabled=gaming.Enabled=reconnect.Enabled=restore.Enabled=!busy;
        battery.Enabled=enableTemperature.Enabled=!busy;
        if(trayMenu!=null) {
            foreach(var item in trayProfiles) item.Enabled=!busy;
            trayRestore.Enabled=trayExit.Enabled=!busy;
            trayManual2000.Enabled=!busy && controller.FanDevice!=null;
        }
    }
    Task<bool> ActionAsync(Action action,string success) {return RunActionAsync(action,delegate {return success;});}
    async Task<bool> RunActionAsync(Action action,Func<string> success) {
        if(busy || closing) return false;
        busy=true;EnableControls();bool ok=false;
        await hardwareGate.WaitAsync();
        try {await Task.Run(action);AddLog(success());ok=true;}
        catch(Exception e) {AddLog("失败："+e.Message);}
        finally {hardwareGate.Release();busy=false;EnableControls();}
        await RefreshAsync();return ok;
    }
    internal async Task RefreshAsync() {
        if(busy || refreshing || closing || screenOff) return;
        refreshing=true;
        await hardwareGate.WaitAsync();
        try {
            string f="",g="",p="",t="";
            await Task.Run(delegate {
                if(screenOff)return;
                try {controller.CheckFanPolicy();}
                catch(Exception e) {Controller.Log("风扇保护恢复失败，下次重试："+e.Message);}
                try {controller.CheckLowFanSafety();}
                catch(Exception e) {controller.LowFanTrialMessage="恢复自动失败，将重试："+e.Message;Controller.Log(controller.LowFanTrialMessage);}
                try {f=controller.FanStatus();} catch(Exception e) {f="读取失败："+e.Message;}
                try {g=controller.GpuStatus();} catch(Exception e) {g="读取失败："+e.Message;}
                t=controller.Temperature.ReadText();
                try {p=controller.CpuStatus();} catch(Exception e) {p="读取失败："+e.Message;}
            });
            UpdateText(fanStatus,f);UpdateText(gpuStatus,g);UpdateText(cpuStatus,p);
            if(inputGuard!=null)UpdateText(inputStatus,inputGuard.Status);
            UpdateText(lowFanStatus,controller.FanPolicyActive?controller.FanPolicyStatus:controller.LowFanTrialMessage);
            UpdateText(temperatureStatus,"CPU 温度："+t+"     GPU 温度："+controller.GpuTemperatureText);
            UpdateText(profileStatus,"上次操作："+controller.ProfileMessage);
            string tip="Blade Control | CPU "+(t.Contains("°C")?t:"--")+" | GPU "+controller.GpuTemperatureText;
            if(tray.Text!=tip) tray.Text=tip;
            UpdateText(connection,(controller.FanDevice!=null?"● 风扇已连接":"○ 风扇未连接")+"    "+(controller.OfficeGpuMode?"● 办公集显模式":controller.GpuId!=null?"● NVIDIA 已连接":"○ NVIDIA 未连接")+(controller.HasChanges?"    ·    存在本程序待恢复设置":"    ·    未接管硬件设置"));
        } finally {hardwareGate.Release();refreshing=false;UpdateTimer();}
    }

}
public static class Program {
    [STAThread] public static int Main(string[] args) {
        bool created;
        using(var mutex=new Mutex(true,@"Local\BladeControl.RZ090528",out created)) {
            if(!created) {MessageBox.Show("Blade Control 已经在运行。");return 1;}
            try {
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                using(var c=new Controller()) {
                    if(args.Length>1 && args[0]=="--gpu-check") {
                        string device=c.GpuDeviceControl.FindNvidia();
                        c.GpuDeviceControl.CheckIntegratedDisplay();
                        File.WriteAllText(args[1],"Integrated display verified; NVIDIA disabled="+c.GpuDeviceControl.IsDisabled(device)+"; device="+device);
                        return 0;
                    }
                    if(args.Length>0 && args[0]=="--diagnose") {
                        c.ConnectFan();c.DetectGpu();
                        string result=RazerHid.Model+Environment.NewLine+c.FanStatus()+Environment.NewLine+c.GpuStatus()+Environment.NewLine+c.CpuStatus();
                        File.WriteAllText(args[1],result);return 0;
                    }
                    bool render=args.Length>0 && args[0]=="--render";
                    bool readOnly=Array.IndexOf(args,"--read-only")>=0;
                    bool startInTray=Array.IndexOf(args,"--tray")>=0;
                    var form=new MainForm(c,render,!readOnly,startInTray,Array.IndexOf(args,"--manual-2000")>=0);
                    if(args.Length>0 && args[0]=="--render") {
                        form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-10000,-10000);
                        form.Show();Application.DoEvents();form.PerformLayout();
                        using(var bitmap=new Bitmap(form.Width,form.Height)) {form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(args[1]);} form.Dispose();return 0;
                    }
                    Application.Run(form);
                }
                return 0;
            } catch(Exception e) {Controller.Log(e.ToString());MessageBox.Show(e.Message,"Blade Control",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}
        }
    }
}
}

