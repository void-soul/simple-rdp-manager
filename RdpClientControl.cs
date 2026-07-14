using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace SimpleRdpManager;

/// <summary>
/// AxHost wrapper for MsRdpClient10 ActiveX control.
/// This is the SAME control RDCMan uses — fully embedded, never floats.
/// CLSID: {C0EFA91A-EEB7-41C7-97FA-F0ED645EFB24} (MsRdpClient10 - Safe for Scripting)
/// </summary>
public class RdpClientControl : AxHost
{
    // MsRdpClient10 CLSID — Safe for Scripting variant
    private const string RdpClsid = "{C0EFA91A-EEB7-41C7-97FA-F0ED645EFB24}";

    private dynamic? _ocx;
    private ServerConfig? _config;
    private bool _isConnected;
    private bool _disposed;
    private bool _reconnecting;
    private Label? _statusLabel;
    private System.Windows.Forms.Timer? _stateTimer;
    private System.Windows.Forms.Timer? _resizeTimer;
    private bool _connectionAttempted;
    private DateTime _connectedAt;
    private int _reconnectWidth;
    private int _reconnectHeight;
    private int _lastReconnectWidth;
    private int _lastReconnectHeight;

    // Minimum remote desktop dimensions (don't reconnect below this)
    private const int MinDesktopWidth = 800;
    private const int MinDesktopHeight = 600;

    // Color constants
    private static readonly Color StatusBg = SystemColors.Control;
    private static readonly Color StatusFg = SystemColors.ControlText;
    private static readonly Color StatusBgError = Color.FromArgb(255, 230, 230);
    private static readonly Color StatusFgError = Color.FromArgb(180, 0, 0);

    public event Action<RdpClientControl>? ConnectionStateChanged;
    public event Action<RdpClientControl, MouseButtons>? MouseClicked;

    public ServerConfig? Config => _config;
    public bool IsConnected => _isConnected;

    public RdpClientControl() : base(RdpClsid)
    {
    }

    // ── AxHost lifecycle ────────────────────────────────
    // AttachInterfaces() is called by AxHost after the OCX is loaded.
    // This is THE safe place to grab the COM reference.

    protected override void AttachInterfaces()
    {
        try
        {
            _ocx = GetOcx();
            Log("AttachInterfaces: OCX 已绑定");
            if (_connectionAttempted && _config != null)
            {
                Log("检测到待处理的连接，启动 DoConnect");
                DoConnect();
            }
        }
        catch (Exception ex)
        {
            Log($"[RDP] AttachInterfaces failed: {ex.Message}");
        }
    }

    // ── Logging ─────────────────────────────────────────

    private static readonly string LogFile = Path.Combine(AppContext.BaseDirectory, "simple-rdp.log");

    private static void Log(string msg)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
            Debug.WriteLine($"[RDP] {line}");
            File.AppendAllText(LogFile, line + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    // ── Status overlay ──────────────────────────────────

    private void ShowStatus(string text, bool isError = false)
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
                ForeColor = StatusFg,
                BackColor = StatusBg,
            };
            // Forward right-clicks to MainForm
            _statusLabel.MouseDown += (_, e) => MouseClicked?.Invoke(this, e.Button);
            Controls.Add(_statusLabel);
        }

        _statusLabel.Text = text;
        _statusLabel.BackColor = isError ? StatusBgError : StatusBg;
        _statusLabel.ForeColor = isError ? StatusFgError : StatusFg;
        _statusLabel.Visible = true;
        _statusLabel.BringToFront();
    }

    private void HideStatus()
    {
        if (InvokeRequired) { BeginInvoke(HideStatus); return; }
        if (_statusLabel is not null)
            _statusLabel.Visible = false;
    }

    // ── Connection API ──────────────────────────────────

    public void ConnectWith(ServerConfig config)
    {
        _config = config;
        _connectionAttempted = true;
        Log($"=== ConnectWith 开始: {config.Ip}:{config.Port} user={config.UserName} ===");
        ShowStatus("正在连接...");

        if (_ocx != null)
        {
            Log("OCX已就绪，直接启动 DoConnect");
            DoConnect();
        }
        else
        {
            if (IsHandleCreated)
            {
                Log("Handle已创建但OCX为空，尝试直接获取 OCX");
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
                    Log($"直接获取 OCX 失败: {ex.Message}");
                }
            }

            Log("OCX未就绪，调用 CreateControl() 等待 AttachInterfaces");
            CreateControl();
        }
    }

    private void DoConnect()
    {
        if (_config is null || _ocx is null) return;

        var pwd = _config.GetPassword();
        if (!string.IsNullOrEmpty(pwd))
        {
            Log("DoConnect: 启动异步凭据存储及连接...");
            // Run StoreCredential on ThreadPool so it doesn't block the UI thread
            ThreadPool.QueueUserWorkItem(_ =>
            {
                StoreCredential();
                // After credential is stored, perform the connection on the UI thread
                BeginInvoke(() => DoConnectUI(pwd));
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

            int baseW = Width > 0 ? Width : 1280;
            int baseH = Height > 0 ? Height : 720;

            int scale = _config.DesktopScale;
            if (scale < 50) scale = 100;
            if (scale > 300) scale = 300;

            int desktopW = (int)(baseW * scale / 100.0);
            int desktopH = (int)(baseH * scale / 100.0);

            // Enforce minimum dimensions while preserving aspect ratio
            if (desktopW < MinDesktopWidth || desktopH < MinDesktopHeight)
            {
                double aspect = (double)baseW / baseH;
                if (aspect >= 1.33) // Landscape-like
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

            // Set initial reconnect width/height to avoid immediately reconnecting
            _lastReconnectWidth = desktopW;
            _lastReconnectHeight = desktopH;

            // ── Step 1: Server & port ──
            ocx.Server = _config.Ip;
            Log($"Server={_config.Ip}");

            // ── Step 2: Desktop size ──
            ocx.DesktopWidth = desktopW;
            ocx.DesktopHeight = desktopH;
            Log($"Desktop={desktopW}x{desktopH}");

            // ── Step 3: User name ──
            if (!string.IsNullOrEmpty(_config.UserName))
            {
                ocx.UserName = _config.UserName;
                Log($"User={_config.UserName}");
            }

            // ── Step 4: Smart Sizing ──
            dynamic adv = ocx.AdvancedSettings9;
            adv.SmartSizing = true;
            Log("SmartSizing=true");
            try
            {
                adv.EnableCredSspSupport = true;
                adv.AuthenticationLevel = 2;
                Log("EnableCredSspSupport=true, AuthenticationLevel=2");
            }
            catch (Exception ex)
            {
                Log($"设置 CredSSP 属性失败: {ex.Message}");
            }

            if (_config.Port > 0 && _config.Port != 3389)
            {
                adv.RDPPort = _config.Port;
                Log($"RDPPort={_config.Port}");
            }

            // ── Step 5: Password ──
            if (!string.IsNullOrEmpty(pwd))
            {
                bool pwdSet = false;

                // Method A: Cast to IMsTscNonScriptable (vtable)
                try
                {
                    var nonScriptable = (IMsTscNonScriptable)_ocx;
                    nonScriptable.put_ClearTextPassword(pwd);
                    pwdSet = true;
                    Log("✓ IMsTscNonScriptable.ClearTextPassword (vtable) 设置成功");
                }
                catch (Exception ex)
                {
                    Log($"✗ IMsTscNonScriptable.ClearTextPassword (vtable) 失败: {ex.Message}");
                }

                if (!pwdSet)
                {
                    // Method B: InvokeMember on AdvancedSettings (forces DISPATCH_PROPERTYPUT)
                    try
                    {
                        var advObj = (object)ocx.AdvancedSettings;
                        advObj.GetType().InvokeMember("ClearTextPassword",
                            BindingFlags.Instance | BindingFlags.PutDispProperty,
                            null, advObj, new object[] { pwd });
                        pwdSet = true;
                        Log("✓ AdvancedSettings.ClearTextPassword (PutDispProperty) 设置成功");
                    }
                    catch (Exception ex)
                    {
                        Log($"✗ PutDispProperty on AdvancedSettings 失败: {ex.Message}");

                        // Fallback: try AdvancedSettings9
                        try
                        {
                            var adv9Obj = (object)adv;
                            adv9Obj.GetType().InvokeMember("ClearTextPassword",
                                BindingFlags.Instance | BindingFlags.PutDispProperty,
                                null, adv9Obj, new object[] { pwd });
                            pwdSet = true;
                            Log("✓ AdvancedSettings9.ClearTextPassword (PutDispProperty) 设置成功");
                        }
                        catch (Exception ex2)
                        {
                            Log($"✗ PutDispProperty on AdvancedSettings9 失败: {ex2.Message}");
                        }
                    }
                }

                // Method C: dynamic fallback
                if (!pwdSet)
                {
                    try { adv.ClearTextPassword = pwd; pwdSet = true; Log("✓ dynamic ClearTextPassword 设置成功"); }
                    catch { Log("✗ 所有密码设置方法均失败，将仅依赖 cmdkey"); }
                }
            }

            // ── Step 6: Connect! ──
            Log("调用 ocx.Connect()...");
            ocx.Connect();
            Log("ocx.Connect() 已返回");
            StartStatePoller();
        }
        catch (Exception ex)
        {
            Log($"DoConnectUI 异常: {ex.Message}");
            ShowStatus($"连接启动失败: {ex.Message}", isError: true);
        }
    }

    // ── Connection state polling ────────────────────────

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

                // Log state transitions
                if (connected != _lastConnectedState)
                {
                    Log($"状态变化: {_lastConnectedState} → {connected} ({_config?.Ip})");
                    
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
                                Log($"读取 ExtendedDisconnectReason 失败: {ex.Message}");
                            }

                            reasonInfo = $"ExtendedDisconnectReason={extCode}";
                        }
                        catch (Exception ex)
                        {
                            reasonInfo = $"Error={ex.Message}";
                        }
                        Log($"连接失败/断开: connected={connected}, {reasonInfo}, ip={_config?.Ip}");
                    }
                    
                    _lastConnectedState = connected;
                }

                bool nowConnected = connected == 1;

                if (nowConnected && !_isConnected)
                {
                    _isConnected = true;
                    _reconnecting = false;
                    _connectedAt = DateTime.Now;
                    Log($"连接成功，设置3秒宽限期禁止Reconnect");
                    BeginInvoke(HideStatus);
                    ConnectionStateChanged?.Invoke(this);
                }
                else if (!nowConnected && _isConnected && !_reconnecting)
                {
                    _isConnected = false;
                    Log($"检测到断开: ip={_config?.Ip}");
                    BeginInvoke(() => ShowStatus("已断开", isError: true));
                    ConnectionStateChanged?.Invoke(this);
                }
                else if (connected == 2 && !_isConnected && _connectionAttempted)
                {
                    BeginInvoke(() =>
                    {
                        if (_statusLabel is not null && _statusLabel.Visible)
                            _statusLabel.Text = "正在连接...";
                    });
                }
            }
            catch
            {
                // OCX may not be ready yet
            }
        };
        _stateTimer.Start();
    }

    // ── Credential management ───────────────────────────

    private void StoreCredential()
    {
        if (_config is null) return;
        var pwd = _config.GetPassword();
        if (string.IsNullOrEmpty(pwd) || string.IsNullOrEmpty(_config.UserName)) return;

        try
        {
            // Remove old
            var del = new ProcessStartInfo
            {
                FileName = "cmdkey",
                Arguments = $"/delete:TERMSRV/{_config.Ip}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(del)?.WaitForExit(3000);

            // Add new — quote password to handle spaces/special chars
            var user = _config.UserName;
            // cmdkey /pass doesn't handle quotes well; use /pass:"..." which works
            var add = new ProcessStartInfo
            {
                FileName = "cmdkey",
                Arguments = $"/generic:TERMSRV/{_config.Ip} /user:\"{user}\" /pass:\"{pwd}\"",
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
                Log($"cmdkey 退出码={proc.ExitCode} stdout={stdout} stderr={stderr}");
            }
        }
        catch (Exception ex)
        {
            Log($"StoreCredential 异常: {ex.Message}");
        }
    }

    // ── Disconnect ──────────────────────────────────────

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

        ShowStatus("未连接");
    }

    // ── Dynamic resolution adjustment ──────────────────

    public void UpdateDesktopScale()
    {
        if (_disposed || IsDisposed || _ocx is null || !_isConnected) return;

        int baseW = Width;
        int baseH = Height;
        if (baseW <= 0 || baseH <= 0) return;

        int w = baseW;
        int h = baseH;

        // Enforce minimum dimensions while preserving aspect ratio
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

        // Don't reconnect if size hasn't changed significantly (50px threshold)
        if (Math.Abs(w - _lastReconnectWidth) < 50 && Math.Abs(h - _lastReconnectHeight) < 50)
            return;

        Log($"UpdateDesktopScale: 控件={Width}x{Height} → 目标={w}x{h}, 上次重连={_lastReconnectWidth}x{_lastReconnectHeight}, 将触发Reconnect");

        _reconnectWidth = w;
        _reconnectHeight = h;

        // Debounce: wait 600ms after last resize before reconnecting
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

        // If we are still in the 3-second grace period, defer the reconnect by restarting the timer!
        double elapsed = (DateTime.Now - _connectedAt).TotalSeconds;
        if (elapsed < 3.0)
        {
            int delay = (int)((3.0 - elapsed) * 1000) + 100;
            Log($"还在连接宽限期内({elapsed:F1}s < 3.0s)，延时 {delay}ms 后再次尝试 Reconnect");
            if (_resizeTimer != null)
            {
                _resizeTimer.Interval = Math.Max(delay, 200);
                _resizeTimer.Start();
            }
            return;
        }

        // Reset interval to default
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

        Log($"DoResolutionReconnect: {w}x{h} (ip={_config?.Ip})");

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

                Log($"尝试平滑无缝调整分辨率: {w}x{h} (scale={scale}%)");
                ocx.UpdateSessionDisplaySettings(
                    (uint)w,
                    (uint)h,
                    (uint)w,
                    (uint)h,
                    0, // Orientation: Landscape
                    (uint)scale,
                    (uint)scale
                );
                Log("平滑分辨率调整成功！");
            }
            catch (Exception ex)
            {
                Log($"平滑调整分辨率失败({ex.Message})，将回退至 Reconnect 重连方式...");
                ocx.Reconnect((uint)w, (uint)h);
            }

            _lastReconnectWidth = w;
            _lastReconnectHeight = h;

            // Re-enable SmartSizing after reconnect (it may have been reset)
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
        // State poller handles periodic refresh
    }

    // ── Mouse click forwarding ──────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        MouseClicked?.Invoke(this, e.Button);
    }

    // ── Cleanup ─────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        _resizeTimer?.Stop();
        _resizeTimer?.Dispose();
        _resizeTimer = null;

        _stateTimer?.Stop();
        _stateTimer?.Dispose();
        _stateTimer = null;

        _isConnected = false;
        _connectionAttempted = false;

        // Let AxHost handle COM cleanup — don't touch _ocx ourselves
        _ocx = null;

        try { _statusLabel?.Dispose(); } catch { }

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
