using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace SimpleRdpManager;

/// <summary>
/// A robust wrapper control for MsRdpClient ActiveX controls.
/// Wraps the actual AxHost in a UserControl to bypass Win32 airspace/z-order painting limitations.
/// </summary>
public class RdpClientControl : UserControl, System.ComponentModel.ISupportInitialize
{
    private RdpClientAxHost? _axHost;
    private Label? _statusLabel;
    private bool _disposed;

    public event Action<RdpClientControl>? ConnectionStateChanged;
    public event Action<RdpClientControl, MouseButtons>? MouseClicked;

    public ServerConfig? Config => _axHost?.Config;
    public bool IsConnected => _axHost?.IsConnected ?? false;

    public RdpClientControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        _axHost = new RdpClientAxHost(this);
        
        ((System.ComponentModel.ISupportInitialize)_axHost).BeginInit();

        _axHost.Dock = DockStyle.Fill;
        _axHost.Visible = false;
        Controls.Add(_axHost);

        _axHost.MouseClicked += (btn) => MouseClicked?.Invoke(this, btn);
        _axHost.ConnectionStateChanged += () => ConnectionStateChanged?.Invoke(this);

        ((System.ComponentModel.ISupportInitialize)_axHost).EndInit();

        ShowStatus("未连接");
    }

    public void BeginInit() => ((System.ComponentModel.ISupportInitialize?)_axHost)?.BeginInit();
    public void EndInit() => ((System.ComponentModel.ISupportInitialize?)_axHost)?.EndInit();

    public void ConnectWith(ServerConfig config)
    {
        _axHost?.ConnectWith(config);
    }

    public void Disconnect()
    {
        _axHost?.Disconnect();
    }

    public void UpdateDesktopScale()
    {
        _axHost?.UpdateDesktopScale();
    }

    public void RefreshConnectionState()
    {
        _axHost?.RefreshConnectionState();
    }

    public void ShowStatus(string text, bool isError = false)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowStatus(text, isError)); return; }
        if (_disposed || IsDisposed) return;

        if (_statusLabel is null)
        {
            _statusLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Regular),
                ForeColor = SystemColors.ControlText,
                BackColor = SystemColors.Control,
            };
            _statusLabel.MouseDown += (_, e) => MouseClicked?.Invoke(this, e.Button);
            Controls.Add(_statusLabel);
        }

        _statusLabel.Text = text;
        _statusLabel.BackColor = isError ? Color.FromArgb(255, 230, 230) : SystemColors.Control;
        _statusLabel.ForeColor = isError ? Color.FromArgb(180, 0, 0) : SystemColors.ControlText;
        _statusLabel.Visible = true;
        _statusLabel.BringToFront();

        if (_axHost != null)
        {
            _axHost.Visible = false;
        }
    }

    public void HideStatus()
    {
        if (InvokeRequired) { BeginInvoke(HideStatus); return; }
        if (_statusLabel is not null)
            _statusLabel.Visible = false;

        if (_axHost != null)
        {
            _axHost.Visible = true;
            _axHost.BringToFront();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            _axHost?.Dispose();
            _statusLabel?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Dynamic ActiveX host wrapper that selects the best registered MsRdpClient CLSID dynamically.
/// </summary>
public class RdpClientAxHost : AxHost
{
    private const string DefaultRdpClsid = "{C0EFA91A-EEB7-41C7-97FA-F0ED645EFB24}";
    private static readonly string DiscoveredClsid = RdpRegistration.DiscoverClsid() ?? DefaultRdpClsid;

    private readonly RdpClientControl _parent;
    private dynamic? _ocx;
    private ServerConfig? _config;
    private bool _isConnected;
    private bool _disposed;
    private bool _reconnecting;
    private System.Windows.Forms.Timer? _stateTimer;
    private System.Windows.Forms.Timer? _resizeTimer;
    private bool _connectionAttempted;
    private DateTime _connectedAt;
    private int _reconnectWidth;
    private int _reconnectHeight;
    private int _lastReconnectWidth;
    private int _lastReconnectHeight;

    private const int MinDesktopWidth = 800;
    private const int MinDesktopHeight = 600;

    public event Action? ConnectionStateChanged;
    public event Action<MouseButtons>? MouseClicked;

    public ServerConfig? Config => _config;
    public bool IsConnected => _isConnected;

    public RdpClientAxHost(RdpClientControl parent) : base(DiscoveredClsid)
    {
        _parent = parent;
        RdpLogger.Log($"RdpClientAxHost 初始化，使用 CLSID: {DiscoveredClsid}");
    }

    protected override void AttachInterfaces()
    {
        try
        {
            _ocx = GetOcx();
            RdpLogger.Log("AttachInterfaces: OCX 已绑定");
            if (_connectionAttempted && _config != null)
            {
                RdpLogger.Log("检测到待处理的连接，启动 DoConnect");
                DoConnect();
            }
        }
        catch (Exception ex)
        {
            RdpLogger.Log($"[RDP] AttachInterfaces failed: {ex.Message}");
        }
    }

    public void ConnectWith(ServerConfig config)
    {
        _config = config;
        _connectionAttempted = true;
        RdpLogger.Log($"=== ConnectWith 开始: {config.Ip}:{config.Port} user={config.UserName} ===");
        _parent.ShowStatus("正在连接...");

        if (_ocx != null)
        {
            RdpLogger.Log("OCX已就绪，直接启动 DoConnect");
            DoConnect();
        }
        else
        {
            if (IsHandleCreated)
            {
                RdpLogger.Log("Handle已创建但OCX为空，尝试直接获取 OCX");
                try
                {
                    _ocx = GetOcx();
                    if (_ocx != null)
                    {
                        DoConnect();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    RdpLogger.Log($"直接获取 OCX 失败: {ex.Message}");
                }
            }

            RdpLogger.Log("OCX未就绪，调用 CreateControl() 等待 AttachInterfaces");
            CreateControl();
        }
    }

    private void DoConnect()
    {
        if (_config is null || _ocx is null) return;

        var pwd = _config.GetPassword();
        if (!string.IsNullOrEmpty(pwd))
        {
            RdpLogger.Log("DoConnect: 启动异步凭据存储及连接...");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                StoreCredential();
                if (!_disposed && !IsDisposed)
                {
                    BeginInvoke(() => DoConnectUI(pwd));
                }
            });
        }
        else
        {
            DoConnectUI(null);
        }
    }

    private void DoConnectUI(string? pwd)
    {
        if (_config is null || _ocx is null || _disposed || IsDisposed) return;

        try
        {
            dynamic ocx = _ocx;

            // Get local scaling factor and calculate physical dimensions
            double localScale = DeviceDpi / 96.0;
            int baseW = _parent.Width > 0 ? (int)(_parent.Width * localScale) : (int)(1280 * localScale);
            int baseH = _parent.Height > 0 ? (int)(_parent.Height * localScale) : (int)(720 * localScale);

            int scale = _config.DesktopScale;
            if (scale < 50) scale = 100;
            if (scale > 300) scale = 300;

            // The session resolution is set to the physical pixels of the container
            int desktopW = baseW;
            int desktopH = baseH;

            if (desktopW < MinDesktopWidth || desktopH < MinDesktopHeight)
            {
                double aspect = (double)baseW / baseH;
                if (aspect >= 1.33)
                {
                    desktopW = MinDesktopWidth;
                    desktopH = (int)(MinDesktopWidth / aspect);
                    if (desktopH < MinDesktopHeight)
                    {
                        desktopH = MinDesktopHeight;
                        desktopW = (int)(MinDesktopHeight * aspect);
                    }
                }
                else
                {
                    desktopH = MinDesktopHeight;
                    desktopW = (int)(MinDesktopHeight * aspect);
                    if (desktopW < MinDesktopWidth)
                    {
                        desktopW = MinDesktopWidth;
                        desktopH = (int)(MinDesktopWidth / aspect);
                    }
                }
            }

            _lastReconnectWidth = desktopW;
            _lastReconnectHeight = desktopH;

            ocx.Server = _config.Ip;
            RdpLogger.Log($"Server={_config.Ip}");

            ocx.DesktopWidth = desktopW;
            ocx.DesktopHeight = desktopH;
            RdpLogger.Log($"Desktop={desktopW}x{desktopH} (DPI localScale={localScale:F2})");

            if (!string.IsNullOrEmpty(_config.UserName))
            {
                ocx.UserName = _config.UserName;
                RdpLogger.Log($"User={_config.UserName}");
            }

            dynamic adv = ocx.AdvancedSettings9;
            adv.SmartSizing = true;
            RdpLogger.Log("SmartSizing=true");
            try
            {
                adv.EnableCredSspSupport = true;
                adv.AuthenticationLevel = 2;
                RdpLogger.Log("EnableCredSspSupport=true, AuthenticationLevel=2");
            }
            catch (Exception ex)
            {
                RdpLogger.Log($"设置 CredSSP 属性失败: {ex.Message}");
            }

            if (_config.Port > 0 && _config.Port != 3389)
            {
                adv.RDPPort = _config.Port;
                RdpLogger.Log($"RDPPort={_config.Port}");
            }

            // Set high DPI properties before connecting
            try
            {
                var extSettings = _ocx as IMsRdpExtendedSettings;
                if (extSettings != null)
                {
                    int deviceScale = scale switch
                    {
                        <= 100 => 100,
                        <= 125 => 140,
                        <= 150 => 140,
                        <= 180 => 180,
                        _ => 180
                    };

                    object desktopScaleVal = (object)scale;
                    extSettings.put_Property("DesktopScaleFactor", ref desktopScaleVal);

                    object deviceScaleVal = (object)deviceScale;
                    extSettings.put_Property("DeviceScaleFactor", ref deviceScaleVal);

                    RdpLogger.Log($"成功应用 High DPI 设置: DesktopScaleFactor={scale}, DeviceScaleFactor={deviceScale}");
                }
            }
            catch (Exception ex)
            {
                RdpLogger.Log($"设置 IMsRdpExtendedSettings 失败: {ex.Message}");
            }

            if (!string.IsNullOrEmpty(pwd))
            {
                bool pwdSet = false;

                try
                {
                    var nonScriptable = (IMsTscNonScriptable)_ocx;
                    nonScriptable.put_ClearTextPassword(pwd);
                    pwdSet = true;
                    RdpLogger.Log("✓ IMsTscNonScriptable.ClearTextPassword (vtable) 设置成功");
                }
                catch (Exception ex)
                {
                    RdpLogger.Log($"✗ IMsTscNonScriptable.ClearTextPassword (vtable) 失败: {ex.Message}");
                }

                if (!pwdSet)
                {
                    try
                    {
                        var advObj = (object)ocx.AdvancedSettings;
                        advObj.GetType().InvokeMember("ClearTextPassword",
                            BindingFlags.Instance | BindingFlags.PutDispProperty,
                            null, advObj, new object[] { pwd });
                        pwdSet = true;
                        RdpLogger.Log("✓ AdvancedSettings.ClearTextPassword (PutDispProperty) 设置成功");
                    }
                    catch (Exception ex)
                    {
                        RdpLogger.Log($"✗ PutDispProperty on AdvancedSettings 失败: {ex.Message}");

                        try
                        {
                            var adv9Obj = (object)adv;
                            adv9Obj.GetType().InvokeMember("ClearTextPassword",
                                BindingFlags.Instance | BindingFlags.PutDispProperty,
                                null, adv9Obj, new object[] { pwd });
                            pwdSet = true;
                            RdpLogger.Log("✓ AdvancedSettings9.ClearTextPassword (PutDispProperty) 设置成功");
                        }
                        catch (Exception ex2)
                        {
                            RdpLogger.Log($"✗ PutDispProperty on AdvancedSettings9 失败: {ex2.Message}");
                        }
                    }
                }

                if (!pwdSet)
                {
                    try { adv.ClearTextPassword = pwd; pwdSet = true; RdpLogger.Log("✓ dynamic ClearTextPassword 设置成功"); }
                    catch { RdpLogger.Log("✗ 所有密码设置方法均失败，将仅依赖 cmdkey"); }
                }
            }

            RdpLogger.Log("调用 ocx.Connect()...");
            ocx.Connect();
            RdpLogger.Log("ocx.Connect() 已返回");
            StartStatePoller();
        }
        catch (Exception ex)
        {
            RdpLogger.Log($"DoConnectUI 异常: {ex.Message}");
            _parent.ShowStatus($"连接启动失败: {ex.Message}", isError: true);
        }
    }

    private int _lastConnectedState = -1;

    private void StartStatePoller()
    {
        _stateTimer?.Stop();
        _stateTimer?.Dispose();

        _stateTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _stateTimer.Tick += (_, _) =>
        {
            if (_disposed || IsDisposed || _ocx is null)
            {
                _stateTimer?.Stop();
                return;
            }

            try
            {
                dynamic ocx = _ocx;
                
                int connected = 0;
                try { connected = Convert.ToInt32(ocx.Connected); } catch { }

                if (connected != _lastConnectedState)
                {
                    RdpLogger.Log($"状态变化: {_lastConnectedState} → {connected} ({_config?.Ip})");
                    
                    if (connected == 0)
                    {
                        string reasonInfo = "N/A";
                        try
                        {
                            var ocxObj = (object)_ocx;
                            var ocxType = ocxObj.GetType();
                            
                            int extCode = -1;
                            try
                            {
                                var res = ocxType.InvokeMember("ExtendedDisconnectReason",
                                    BindingFlags.GetProperty, null, ocxObj, null);
                                if (res != null) extCode = (int)res;
                            }
                            catch (Exception ex)
                            {
                                RdpLogger.Log($"读取 ExtendedDisconnectReason 失败: {ex.Message}");
                            }

                            reasonInfo = $"ExtendedDisconnectReason={extCode}";
                        }
                        catch (Exception ex)
                        {
                            reasonInfo = $"Error={ex.Message}";
                        }
                        RdpLogger.Log($"连接失败/断开: connected={connected}, {reasonInfo}, ip={_config?.Ip}");
                    }
                    
                    _lastConnectedState = connected;
                }

                bool nowConnected = connected == 1;

                if (nowConnected && !_isConnected)
                {
                    _isConnected = true;
                    _reconnecting = false;
                    _connectedAt = DateTime.Now;
                    RdpLogger.Log($"连接成功，设置3秒宽限期禁止Reconnect");
                    _parent.BeginInvoke(_parent.HideStatus);
                    ConnectionStateChanged?.Invoke();
                }
                else if (!nowConnected && _isConnected && !_reconnecting)
                {
                    _isConnected = false;
                    RdpLogger.Log($"检测到断开: ip={_config?.Ip}");
                    _parent.BeginInvoke(() => _parent.ShowStatus("已断开", isError: true));
                    ConnectionStateChanged?.Invoke();
                }
                else if (connected == 2 && !_isConnected && _connectionAttempted)
                {
                    _parent.BeginInvoke(() =>
                    {
                        _parent.ShowStatus("正在连接...");
                    });
                }
            }
            catch
            {
            }
        };
        _stateTimer.Start();
    }

    private void StoreCredential()
    {
        if (_config is null) return;
        var pwd = _config.GetPassword();
        if (string.IsNullOrEmpty(pwd) || string.IsNullOrEmpty(_config.UserName)) return;

        try
        {
            string targetKey = _config.Port == 3389 ? _config.Ip : $"{_config.Ip}:{_config.Port}";

            var del = new ProcessStartInfo
            {
                FileName = "cmdkey",
                Arguments = $"/delete:TERMSRV/{targetKey}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(del)?.WaitForExit(3000);

            var user = _config.UserName;
            var add = new ProcessStartInfo
            {
                FileName = "cmdkey",
                Arguments = $"/generic:TERMSRV/{targetKey} /user:\"{user}\" /pass:\"{pwd}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var proc = Process.Start(add);
            if (proc != null)
            {
                proc.WaitForExit(3000);
                var stdout = proc.StandardOutput.ReadToEnd().Trim();
                var stderr = proc.StandardError.ReadToEnd().Trim();
                RdpLogger.Log($"cmdkey 退出码={proc.ExitCode} stdout={stdout} stderr={stderr}");
            }
        }
        catch (Exception ex)
        {
            RdpLogger.Log($"StoreCredential 异常: {ex.Message}");
        }
    }

    public void Disconnect()
    {
        _stateTimer?.Stop();
        _stateTimer?.Dispose();
        _stateTimer = null;
        _isConnected = false;
        _connectionAttempted = false;

        try
        {
            if (_ocx is not null)
            {
                dynamic ocx = _ocx;
                int connected = 0;
                try { connected = Convert.ToInt32(ocx.Connected); } catch { }
                if (connected != 0)
                    ocx.Disconnect();
            }
        }
        catch { }

        _parent.ShowStatus("未连接");
    }

    public void UpdateDesktopScale()
    {
        if (_disposed || IsDisposed || _ocx is null || !_isConnected) return;

        double localScale = DeviceDpi / 96.0;
        int baseW = (int)(_parent.Width * localScale);
        int baseH = (int)(_parent.Height * localScale);
        if (baseW <= 0 || baseH <= 0) return;

        int w = baseW;
        int h = baseH;

        if (w < MinDesktopWidth || h < MinDesktopHeight)
        {
            double aspect = (double)baseW / baseH;
            if (aspect >= 1.33)
            {
                w = MinDesktopWidth;
                h = (int)(MinDesktopWidth / aspect);
                if (h < MinDesktopHeight)
                {
                    h = MinDesktopHeight;
                    w = (int)(MinDesktopHeight * aspect);
                }
            }
            else
            {
                h = MinDesktopHeight;
                w = (int)(MinDesktopHeight * aspect);
                if (w < MinDesktopWidth)
                {
                    w = MinDesktopWidth;
                    h = (int)(MinDesktopWidth / aspect);
                }
            }
        }

        if (Math.Abs(w - _lastReconnectWidth) < 50 && Math.Abs(h - _lastReconnectHeight) < 50)
            return;

        RdpLogger.Log($"UpdateDesktopScale: 控件={_parent.Width}x{_parent.Height} (DPI Scale={localScale:F2}) → 目标={w}x{h}, 上次重连={_lastReconnectWidth}x{_lastReconnectHeight}, 将触发Reconnect");

        _reconnectWidth = w;
        _reconnectHeight = h;

        if (_resizeTimer is null)
        {
            _resizeTimer = new System.Windows.Forms.Timer { Interval = 600 };
            _resizeTimer.Tick += OnResizeTimerTick;
        }
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void OnResizeTimerTick(object? sender, EventArgs e)
    {
        _resizeTimer?.Stop();

        double elapsed = (DateTime.Now - _connectedAt).TotalSeconds;
        if (elapsed < 3.0)
        {
            int delay = (int)((3.0 - elapsed) * 1000) + 100;
            RdpLogger.Log($"还在连接宽限期内({elapsed:F1}s < 3.0s)，延时 {delay}ms 后再次尝试 Reconnect");
            if (_resizeTimer != null)
            {
                _resizeTimer.Interval = Math.Max(delay, 200);
                _resizeTimer.Start();
            }
            return;
        }

        if (_resizeTimer != null)
            _resizeTimer.Interval = 600;

        DoResolutionReconnect();
    }

    private void DoResolutionReconnect()
    {
        if (_disposed || IsDisposed || _ocx is null || !_isConnected) return;
        if (_reconnectWidth <= 0 || _reconnectHeight <= 0) return;

        int w = _reconnectWidth;
        int h = _reconnectHeight;

        RdpLogger.Log($"DoResolutionReconnect: {w}x{h} (ip={_config?.Ip})");

        try
        {
            _reconnecting = true;

            dynamic ocx = _ocx;
            int connected = 0;
            try { connected = Convert.ToInt32(ocx.Connected); } catch { }
            if (connected != 1) { _reconnecting = false; return; }

            Debug.WriteLine($"[RDP] Reconnect to {w}x{h}");
            try
            {
                int scale = _config?.DesktopScale ?? 100;
                if (scale < 50) scale = 100;
                if (scale > 300) scale = 300;

                int deviceScale = scale switch
                {
                    <= 100 => 100,
                    <= 125 => 140,
                    <= 150 => 140,
                    <= 180 => 180,
                    _ => 180
                };

                RdpLogger.Log($"尝试平滑无缝调整分辨率: {w}x{h} (scale={scale}%)");
                ocx.UpdateSessionDisplaySettings(
                    (uint)w,
                    (uint)h,
                    (uint)w,
                    (uint)h,
                    0,
                    (uint)scale,
                    (uint)deviceScale
                );
                RdpLogger.Log("平滑分辨率调整成功！");
            }
            catch (Exception ex)
            {
                RdpLogger.Log($"平滑调整分辨率失败({ex.Message})，将回退至 Reconnect 重连方式...");
                ocx.Reconnect((uint)w, (uint)h);
            }

            _lastReconnectWidth = w;
            _lastReconnectHeight = h;

            try
            {
                dynamic adv = ocx.AdvancedSettings9;
                adv.SmartSizing = true;
            }
            catch { }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RDP] Reconnect failed: {ex.Message}");
        }
        finally
        {
            _reconnecting = false;
        }
    }

    public void RefreshConnectionState()
    {
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        MouseClicked?.Invoke(e.Button);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            Disconnect();
        }

        _resizeTimer?.Stop();
        _resizeTimer?.Dispose();
        _resizeTimer = null;

        _stateTimer?.Stop();
        _stateTimer?.Dispose();
        _stateTimer = null;

        _ocx = null;

        base.Dispose(disposing);
    }
}

// ── COM Interface for non-scriptable password ─────────
[ComImport]
[Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsTscNonScriptable
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.InternalCall, MethodCodeType = System.Runtime.CompilerServices.MethodCodeType.Runtime)]
    void put_ClearTextPassword([In, MarshalAs(UnmanagedType.BStr)] string pPassword);
}

// ── COM Interface for extended settings (DPI Scaling) ──
[ComImport]
[Guid("302D8188-0052-4807-806A-362B628F9AC5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpExtendedSettings
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.InternalCall, MethodCodeType = System.Runtime.CompilerServices.MethodCodeType.Runtime)]
    void put_Property([In, MarshalAs(UnmanagedType.BStr)] string bstrPropertyName, [In] ref object pValue);
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.InternalCall, MethodCodeType = System.Runtime.CompilerServices.MethodCodeType.Runtime)]
    void get_Property([In, MarshalAs(UnmanagedType.BStr)] string bstrPropertyName, [Out] out object pValue);
}
