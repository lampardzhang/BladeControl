# Blade Control 1.20

用于 Razer Blade 16 2025（RZ09-0528 / Ryzen AI 9 365 / RTX 5070 Ti Laptop）的 Windows 控制程序。使用 C#、Windows Forms 和系统自带 .NET Framework 编译器。

## 功能

- 办公、普通、游戏模式，主窗口与托盘菜单均可切换。
- 办公 CPU 插电上限 1800 MHz、电池上限 1600 MHz；普通/游戏使用动态频率。
- 办公 CPU 达到 50°C 时风扇目标 2000 RPM，低于 48°C 持续 30 秒后交回原厂自动；普通 2500 RPM，游戏 3900 RPM。
- CPU 温度、风扇实际与目标转速显示，局部刷新；已移除所有独显温度读取。办公模式不轮询 NVIDIA，普通/游戏显示频率和利用率。
- CPU 80°C、CPU 传感器或风扇异常时尝试恢复自动散热。不提供 GPU 温度触发的软件保护。
- 合盖/熄屏暂停采样、恢复原厂控温，并关闭蓝牙及可独立禁用的外接输入；亮屏恢复原状态。蓝牙耳机也会断开。
- 正常退出恢复接管设置；持久恢复记录用于异常退出后的恢复。

硬件接口针对上述型号编写，不是通用笔记本风扇工具。低于 2000 RPM 的手动请求属于限时实验，固件不一定执行。用户态温控不能替代原厂硬件保护。

## 构建

在 Windows PowerShell 中运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

生成 `BladeControl.exe`。运行时要求管理员权限。启动参数 `--office --tray` 默认办公并进入托盘；`--read-only` 不自动应用模式。

## 温度组件

源码仓库不包含第三方驱动安装包或预编译传感器模块。构建本身不需要它们，运行 CPU 温度功能需要以下文件以及已安装的 PawnIO 驱动：

- `sensors/AMDFamily17.bin`：与本机适配的 PawnIO 模块。
- `drivers/PawnIO_setup.exe`：如使用界面中的安装功能，安装包 SHA-256 必须为 `A3A46226C5E2824F4CDD42BE0EECBABFC672C86F7889710F5AB1E6AD385B47A0`。

已有完整安装目录时，将生成的程序与 `src/BluetoothRadio.ps1` 按原有目录结构使用，并保留该目录的驱动、sensors 文件。缺少温度组件时，软件温控会报错并尝试交回原厂自动。

第三方许可和来源见 `THIRD_PARTY_LICENSES.md`、`NOTICE.txt`、`R-Helper-LICENSE.txt` 和 `licenses/`。本次未为自有代码新增开源许可证。

## 验证

```powershell
.\tests\run-tests.ps1 -OutputDirectory .\test-results\logic
.\tests\run-ui-tests.ps1 -OutputDirectory .\test-results\ui
.\tests\run-input-tests.ps1 -OutputDirectory .\test-results\input
```

逻辑和界面检查使用模拟风扇写入，不等同于所有硬件状态已实测。`tests` 中的硬件试验程序会操作真实硬件，需明确了解后运行。

不包含本机日志、电源恢复状态、开机任务、账号信息或历史安装备份。
