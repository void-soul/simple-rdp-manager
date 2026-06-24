# Simple RDP Manager

简易宫格 RDP 管理器 — 一个轻量级的 Windows 远程桌面多窗口管理工具。

## 功能特性

- **宫格布局**：多台远程桌面以网格形式平铺显示，一目了然
- **单窗口放大**：点击任意桌面可将其放大，其余窗口收至侧边栏
- **动态分辨率**：窗口大小变化时自动调整远程桌面分辨率
- **服务器管理**：支持添加、删除、编辑远程桌面配置
- **密码加密**：使用 Windows DPAPI 加密存储密码（`ProtectedData`）
- **全屏模式**：按 `F11` 切换全屏显示
- **连接状态**：实时显示每台远程桌面的连接状态
- **多控件兼容**：支持 MsRdpClient7 ~ MsRdpClient10 系列 ActiveX 控件

## 系统要求

- Windows 10 / Windows 11
- [.NET Desktop Runtime 10.0](https://dotnet.microsoft.com/download/dotnet/10.0)
- 已安装远程桌面 ActiveX 控件（通常系统自带，Windows Home 版可能需要手动安装）

## 快速开始

### 下载运行

从 [Releases](https://github.com/YOUR_USER/simple-rdp-manager/releases) 页面下载最新版本，解压后运行 `SimpleRdpManager.exe`。

### 从源码构建

```bash
# 安装 .NET 10 SDK
# 克隆仓库
git clone https://github.com/YOUR_USER/simple-rdp-manager.git
cd simple-rdp-manager

# 构建
dotnet build -c Release

# 发布（生成单文件夹）
dotnet publish -c Release -o publish
```

## 使用说明

1. 启动程序后，点击工具栏 **"+ 添加"** 按钮添加远程桌面
2. 填写名称、IP 地址、端口（默认 3389）、用户名和密码
3. 点击保存后自动连接，多台桌面以宫格布局显示
4. 点击工具栏上的服务器名称按钮或网格中的桌面，可放大/还原单个桌面
5. 右键点击服务器按钮可编辑配置
6. 按 `F11` 可切换到全屏模式

## 技术栈

- C# / .NET 10 (Windows Forms)
- MsRdpClient ActiveX 控件（Windows 原生 RDP 组件）
- Windows DPAPI 密码加密

## 许可证

[MIT License](LICENSE)
