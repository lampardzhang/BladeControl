using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BladeControl {
internal interface IRadioControl {
    string State(int timeout);
    void Set(string state,int timeout);
}
// WinRT reflection uses the framework's projection without needing a Windows SDK
// at build time. No child process is started on the suspend path.
internal sealed class NativeBluetoothRadio : IRadioControl {
    internal static readonly Lazy<NativeBluetoothRadio> Shared=new Lazy<NativeBluetoothRadio>(delegate{return new NativeBluetoothRadio();});
    readonly Type radioType,accessType,stateType;
    readonly MethodInfo asTask;
    readonly object radio;
    Task pending;
    string target;
    static int Left(Stopwatch watch,int timeout) {int left=timeout-(int)watch.ElapsedMilliseconds;if(left<=0)throw new TimeoutException("Bluetooth operation deadline exceeded");return left;}
    Task ConvertOperation(object operation,Type result) {return (Task)asTask.MakeGenericMethod(result).Invoke(null,new[]{operation});}
    static object Result(Task task,int timeout) {
        if(!task.Wait(timeout))throw new TimeoutException("Bluetooth API timed out");
        return task.GetType().GetProperty("Result").GetValue(task,null);
    }
    internal NativeBluetoothRadio() {
        radioType=Type.GetType("Windows.Devices.Radios.Radio, Windows.System.Devices, ContentType=WindowsRuntime",true);
        accessType=Type.GetType("Windows.Devices.Radios.RadioAccessStatus, Windows.System.Devices, ContentType=WindowsRuntime",true);
        stateType=Type.GetType("Windows.Devices.Radios.RadioState, Windows.System.Devices, ContentType=WindowsRuntime",true);
        var extensions=Assembly.LoadFrom(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),"System.Runtime.WindowsRuntime.dll")).GetType("System.WindowsRuntimeSystemExtensions");
        foreach(var method in extensions.GetMethods())if(method.Name=="AsTask" && method.IsGenericMethodDefinition && method.GetParameters().Length==1 && method.GetParameters()[0].ParameterType.Name=="IAsyncOperation`1"){asTask=method;break;}
        if(asTask==null)throw new Exception("WinRT task projection unavailable");
        var list=(IEnumerable)Result(ConvertOperation(radioType.GetMethod("GetRadiosAsync").Invoke(null,null),typeof(IReadOnlyList<>).MakeGenericType(radioType)),5000);
        foreach(object candidate in list)if(radioType.GetProperty("Kind").GetValue(candidate,null).ToString()=="Bluetooth") {
            if(radio!=null)throw new Exception("Multiple Bluetooth radios; no radio changed");radio=candidate;
        }
        if(radio!=null && Result(ConvertOperation(radioType.GetMethod("RequestAccessAsync").Invoke(null,null),accessType),5000).ToString()!="Allowed")throw new Exception("Bluetooth radio access denied");
        // Warm the getter and mutation method reflection before any power notification.
        if(radio!=null)ReadState();
        Controller.Log("蓝牙接口：已预加载，状态="+State(1000));
    }
    string ReadState() {
        if(radio==null)return "Off";
        string state=radioType.GetProperty("State").GetValue(radio,null).ToString();
        if(state=="Disabled")return "Off";
        if(state!="On" && state!="Off")throw new Exception("Bluetooth state unknown");
        return state;
    }
    void Finish(Stopwatch watch,int timeout) {
        if(pending==null)return;
        string result;
        try {result=Result(pending,Left(watch,timeout)).ToString();}
        catch {if(pending.IsCompleted)pending=null;throw;}
        if(result!="Allowed"){pending=null;throw new Exception("Bluetooth request rejected: "+result);}
        while(ReadState()!=target)Thread.Sleep(Math.Min(10,Left(watch,timeout)));
        pending=null;
    }
    public string State(int timeout) {var watch=Stopwatch.StartNew();Finish(watch,timeout);return ReadState();}
    public void Set(string state,int timeout) {
        var watch=Stopwatch.StartNew();Finish(watch,timeout);
        if(ReadState()==state)return;
        if(radio==null)throw new Exception("Bluetooth radio missing during restore");
        Left(watch,timeout);target=state;
        pending=ConvertOperation(radioType.GetMethod("SetStateAsync").Invoke(radio,new[]{Enum.Parse(stateType,state)}),accessType);
        Finish(watch,timeout);
    }
}
internal sealed class BluetoothSleepGuard {
    readonly IRadioControl radio;
    readonly string path;
    readonly object operation=new object();
    bool owned;
    internal string Status="蓝牙保护：已准备";
    internal BluetoothSleepGuard(IRadioControl radio,string path) {
        this.radio=radio;this.path=path;Directory.CreateDirectory(Path.GetDirectoryName(path));
        if(File.Exists(path)) {string value=File.ReadAllText(path).Trim();if(value!="0" && value!="1")throw new Exception("Invalid Bluetooth recovery record");owned=value=="1";}
    }
    void Save() {
        string temp=path+".tmp";
        using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){stream.WriteByte(owned?(byte)'1':(byte)'0');stream.Flush(true);}
        if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
    }
    static int Left(Stopwatch watch) {int left=1400-(int)watch.ElapsedMilliseconds;if(left<=0)throw new TimeoutException("Suspend preparation exceeded 1400 ms");return left;}
    // Called directly on the power callback, with no Task.Run, enumeration or shell launch.
    internal bool Protect(string reason) {
        var watch=Stopwatch.StartNew();
        if(!Monitor.TryEnter(operation,100)){Status="蓝牙关闭未确认：接口正忙";Controller.Log(reason+" "+Status);return false;}
        try {
            string state=radio.State(Left(watch));
            if(state!="Off") {
                if(!owned){owned=true;Save();}
                radio.Set("Off",Left(watch));
            }
            if(radio.State(Left(watch))!="Off")throw new Exception("Bluetooth off readback failed");
            Status="蓝牙已回读确认关闭";Controller.Log(reason+" "+Status+"；耗时="+watch.ElapsedMilliseconds+" ms");return true;
        }catch(Exception error){Status="蓝牙关闭未确认："+error.Message;Controller.Log(reason+" "+Status+"；耗时="+watch.ElapsedMilliseconds+" ms；保留恢复记录");return false;}
        finally{Monitor.Exit(operation);}
    }
    internal void Restore(Func<bool> allowed) {
        lock(operation) {
            if(!allowed() || !owned)return;
            // Drain any timed-out off operation before inspecting state or issuing On.
            if(radio.State(5000)!="On")radio.Set("On",5000);
            if(radio.State(1000)!="On")throw new Exception("Bluetooth on readback failed");
            owned=false;Save();Status="蓝牙已恢复开启";Controller.Log(Status);
        }
    }
}
}
