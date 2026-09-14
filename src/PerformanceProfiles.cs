using System;
namespace BladeControl {
public sealed class PerformancePreset {
    public readonly string Name;
    public readonly int CpuMhz,GpuMhz,FanRpm;
    public readonly bool IntegratedOnly; public string FanLabel {get {return FanRpm==0?"低温自动 / 散热 2000 RPM":FanRpm+" RPM";}}
    public PerformancePreset(string name,int cpu,int gpu,int fan,bool integratedOnly=false) {Name=name;CpuMhz=cpu;GpuMhz=gpu;FanRpm=fan;IntegratedOnly=integratedOnly;}
}
public sealed class ProfileResult {
    public bool Complete;
    public string Message;
}
public static class PerformanceProfiles {
    public static readonly PerformancePreset Office=new PerformancePreset("办公",1800,0,0,true);
    public static readonly PerformancePreset Normal=new PerformancePreset("普通",0,0,2500);
    public static readonly PerformancePreset Gaming=new PerformancePreset("游戏",0,0,3900);
    // Each operation reports its own outcome. Never report a fully applied profile
    // when a mobile GPU driver rejects its part of the request.
    internal static ProfileResult Apply(PerformancePreset preset,Action<int> cpu,Action<int> gpu,Action resetGpu,Action<int> fan,Action officeGpu) {
        // Apply cooling first. Do not raise performance if the fan command fails.
        fan(preset.FanRpm);
        try {cpu(preset.CpuMhz);}
        catch(Exception e) {return new ProfileResult {Complete=false,Message=preset.Name+"风扇已设为 "+preset.FanLabel+"；CPU 未完成："+e.Message+"；GPU 未切换"};}
        try {
            if(preset.IntegratedOnly) officeGpu();else if(preset.GpuMhz==0) resetGpu();else gpu(preset.GpuMhz);
            return new ProfileResult {Complete=true,Message=preset.Name+"已应用：风扇 "+preset.FanLabel+"；CPU "+(preset.CpuMhz==0?"动态睿频":preset.CpuMhz+" MHz 上限")+"；GPU "+(preset.IntegratedOnly?"AMD 集显输出，不读取独显温度":preset.GpuMhz==0?"动态频率":preset.GpuMhz+" MHz")};
        } catch(Exception e) {
            return new ProfileResult {Complete=false,Message=preset.Name+"风扇 "+preset.FanLabel+"、CPU 已应用；GPU 未完成："+e.Message};
        }
    }
}
}
