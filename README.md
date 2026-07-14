# Simple RDP Manager (简易宫格 RDP 管理器)

一个轻量、高效、DPI 自适应的 Windows 远程桌面（RDP）多窗口宫格管理器。基于 C# .NET 10 (Windows Forms) 开发，完美嵌入原生 Windows 远程桌面 ActiveX 控件，提供极佳的连接速度与操控体验。

## 功能特性

- **🖥️ 宫格网格布局**：支持多台远程桌面以网格平铺展示，大屏监控一目了然。支持单窗口双击/点击放大，其余窗口自动收纳至侧边栏。
- **✨ 高清 DPI 自适应（极清显示）**：自动识别本地显示器 DPI 缩放比（DeviceDpi），将 RDP 连接分辨率映射到物理像素，并原生设置远程桌面 `DesktopScaleFactor`，彻底消除高分屏下图像拉伸导致的字体模糊。
- **🔌 动态 CLSID 版本兼容**：自动扫描并加载系统注册的 MsRdpClient10 ~ MsRdpClient7 系列 ActiveX 控件（含 Safe / NotSafeForScripting 变体），无缝向下兼容，避免组件缺失引发的崩溃。
- **🔒 安全密码存储 (DPAPI)**：使用 Windows DPAPI (`ProtectedData.Protect`) 加密存储用户密码，保障本地凭据安全；结合 Windows 凭据管理器，支持无密一键式安全连接。
- **📡 端口级凭据隔离**：在 Windows 凭据管理器中以 `TERMSRV/{IP}:{Port}` 进行隔离存储，解决多台远程设备共享同一公网 IP（NAT/端口映射环境）时的凭据覆盖冲突。
- **🛸 无缝分辨率重连**：当窗口大小改变或切换全屏（F11 键）时，智能防抖动（Debounce）触发平滑调整，自适应调校远程桌面分辨率。
- **⚡ 防闪烁布局引擎**：在窗体级启用双缓冲（DoubleBuffered），在重排宫格时自动锁定组件树布局（SuspendLayout），减少多 RDP 控件并发重绘产生的剧烈闪烁。
- **🧩 彻底解决 Airspace 物理遮挡**：通过 `UserControl` 容器嵌套机制，规避了 Win32 原生 ActiveX 控件对 lightweight WinForms 控件（如 Label）的绝对图层遮挡，使得“正在连接...”、“已断开”等提示信息稳定清晰呈现。
- **📂 生产级健壮性设计**：将运行日志写入 `%LocalAppData%\SimpleRdpManager` 目录，避免程序安装在 `Program Files` 等只读目录时因写日志权限不足导致崩溃。

## 系统要求

- Windows 10 / Windows 11 (64-bit / 32-bit)
- [.NET Desktop Runtime 10.0](https://dotnet.microsoft.com/download/dotnet/10.0)
- 系统已装载远程桌面 ActiveX 控件（Windows 专业版/企业版系统默认自带）

## 快速开始

### 运行 Release 版本
从 [Releases](https://github.com/YOUR_USER/simple-rdp-manager/releases) 页面下载最新压缩包，解压后双击运行 `SimpleRdpManager.exe` 即可。

### 从源码构建
```bash
# 克隆仓库
git clone https://github.com/YOUR_USER/simple-rdp-manager.git
cd simple-rdp-manager

# 还原并编译
dotnet build -c Release

# 发布为单文件便携版
dotnet publish -c Release -o publish
```

## 使用说明

1. **添加设备**：启动程序后，点击工具栏 **"+ 添加"** 按钮。
2. **自适应缩放**：输入设备名称、IP 地址、端口、用户名和密码。缩放框会**自动探测**并预选您当前屏幕的最佳 DPI 缩放比。保存后会自动进行连接。
3. **快捷交互**：
   - 双击或单击网格中的任意设备画面，可将其放大为焦点主窗，其他设备自动缩至右侧。
   - 右键点击工具栏上的服务器按钮，可随时进行**重新连接**、**断开连接**或**修改配置**。
   - 按 **`F11`** 键进入全屏模式，鼠标移动到屏幕最上方会自动滑出工具栏，下移后自动隐藏，沉浸式操控。
4. **日志排障**：如遇连接失败，可访问 `C:\Users\<您的用户名>\AppData\Local\SimpleRdpManager\logs\simple-rdp.log` 查看详细错误日志与状态转换事件。

## 许可证

基于 [MIT License](LICENSE) 许可证开源。
