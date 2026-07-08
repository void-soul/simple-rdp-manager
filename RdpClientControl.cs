using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SimpleRdpManager;

/// <summary>
/// AxHost wrapper for MsRdpClient10 ActiveX control.
/// This is the SAME control RDCMan uses — fully embedded, never floats.
/// CLSID: {C0EFA91A-EEB7-41C7-97FA-F0ED645EFB24} (MsRdpClient10 - Safe for Scripting)
/// </summary>
public class RdpClientControl : AxHost
{
    // MsRdpClient10 CLSID — confirmed working on this system via CoCreateInstance
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
    private int _reconnectWidth;
    private int _reconnectHeight;
    private int _lastReconnectWidth;
    private int _lastReconnectHeight;

    // Minimum remote desktop dimensions (don't reconnect below this)
    private const int MinDesktopWidth = 800;
    private const int MinDesktopHeight = 600;

    // Color constants
    private static readonly Color BgColor = Color.FromArgb(20, 20, 20);
    private static readonly Color StatusBg = Color.FromArgb(30, 30, 30);
    private static readonly Color StatusFg = Color.FromArgb(200, 200, 50);
    private static readonly Color StatusBgError = Color.FromArgb(60, 20, 20);
    private static readonly Color StatusFgError = Color.FromArgb(255, 100, 100);

    public event Action<RdpClientControl>? ConnectionStateChanged;
    public event Action<RdpClientControl, MouseButtons>? MouseClicked;

    public ServerConfig? Config => _config;
    public bool IsConnected => _isConnected;

    public RdpClientControl() : base(RdpClsid)
    {
        BackColor = BgColor;
    }

    // ── AxHost lifecycle ────────────────────────────────
    // AttachInterfaces() is called by AxHost after the OCX is loaded.
    // This is THE safe place to grab the COM reference.

    protected override void AttachInterfaces()
    {
        try
        {
            _ocx = GetOcx();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RDP] AttachInterfaces failed: {ex.Message}");
        }
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
        ShowStatus("正在连接...");

        // CRITICAL: CreateControl() triggers handle creation → OCX loading → AttachInterfaces() → _ocx set
        if (!IsHandleCreated)
            CreateControl();

        // AxHost COM OCX loading is asynchronous — pump messages with timeout (max ~5 seconds)
        int maxRetries = 250; // 250 * 20ms = 5000ms
        for (int i = 0; i < maxRetries && _ocx is null; i++)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }

        if (_ocx is null)
        {
            ShowStatus($"RDP控件创建失败 - 系统可能未安装远程桌面ActiveX控件", isError: true);
            Debug.WriteLine($"[RDP] OCX creation timed out after {maxRetries * 20}ms, CLSID={RdpClsid}");
            return;
        }

        // Offload the actual connection to a background thread
        // (the COM object itself is thread-affine, but property setting is fast)
        ThreadPool.QueueUserWorkItem(_ => DoConnect());
    }

    private void DoConnect()
    {
        if (_config is null || _ocx is null) return;

        try
        {
            dynamic ocx = _ocx;

            // Initial resolution: use config scale ratio, then UpdateDesktopScale() will
            // reconnect to exact control size once connected and laid out.
            int scale = _config.DesktopScale;
            if (scale < 50) scale = 100;
            if (scale > 300) scale = 300;
            int desktopW = Math.Max((int)(1280 * scale / 100.0), MinDesktopWidth);
            int desktopH = Math.Max((int)(720 * scale / 100.0), MinDesktopHeight);

            // ── Step 1: Server & port ──
            ocx.Server = _config.Ip;

            // ── Step 2: Desktop size ──
            ocx.DesktopWidth = desktopW;
            ocx.DesktopHeight = desktopH;

            // ── Step 3: User name ──
            if (!string.IsNullOrEmpty(_config.UserName))
                ocx.UserName = _config.UserName;

            // ── Step 4: Smart Sizing (auto-scale to fit the container) ──
            dynamic adv = ocx.AdvancedSettings9;
            adv.SmartSizing = true;

            // Port
            if (_config.Port > 0 && _config.Port != 3389)
                adv.RDPPort = _config.Port;

            // ── Step 5: Password ──
            // MsRdpClient10 is the "Safe" variant — ClearTextPassword is on the
            // non-scriptable interface, not the default. We use cmdkey as primary
            // and the non-scriptable interface as fallback.
            var pwd = _config.GetPassword();
            if (!string.IsNullOrEmpty(pwd))
            {
                // Store in Windows Credential Manager (most reliable for Safe variant)
                StoreCredential();

                // Also try non-scriptable interface as direct fallback
                try
                {
                    // The non-scriptable interface is accessible via QueryInterface
                    // Cast to the correct interface
                    var nonScriptable = (IMsRdpClientNonScriptable5)ocx;
                    nonScriptable.ClearTextPassword = pwd;
                }
                catch
                {
                    // Fallback: try through AdvancedSettings
                    try { adv.ClearTextPassword = pwd; } catch { }
                }
            }

            // ── Step 6: Connect! ──
            BeginInvoke(() =>
            {
                try
                {
                    ocx.Connect();
                    StartStatePoller();
                }
                catch (Exception ex)
                {
                    ShowStatus($"连接启动失败: {ex.Message}", isError: true);
                }
            });
        }
        catch (Exception ex)
        {
            BeginInvoke(() => ShowStatus($"配置失败: {ex.Message}", isError: true));
        }
    }

    // ── Connection state polling ────────────────────────

    private void StartStatePoller()
    {
        _stateTimer?.Stop();
        _stateTimer?.Dispose();

        _stateTimer = new System.Windows.Forms.Timer { Interval = 1500 };
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
                int connected = ocx.Connected;       // 0=not connected, 1=connecting, 2=connected

                bool nowConnected = connected >= 2;

                if (nowConnected && !_isConnected)
                {
                    _isConnected = true;
                    _reconnecting = false;
                    BeginInvoke(HideStatus);
                    ConnectionStateChanged?.Invoke(this);
                }
                else if (!nowConnected && _isConnected && !_reconnecting)
                {
                    _isConnected = false;
                    BeginInvoke(() => ShowStatus("已断开"));
                    ConnectionStateChanged?.Invoke(this);
                }
                else if (!nowConnected && !_isConnected && _connectionAttempted)
                {
                    string status = connected switch
                    {
                        0 => "未连接",
                        1 => "正在连接...",
                        _ => "未知状态"
                    };
                    BeginInvoke(() =>
                    {
                        if (_statusLabel is not null && _statusLabel.Visible)
                            _statusLabel.Text = status;
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

            // Add new
            var add = new ProcessStartInfo
            {
                FileName = "cmdkey",
                Arguments = $"/generic:TERMSRV/{_config.Ip} /user:{_config.UserName} /pass:{pwd}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(add)?.WaitForExit(3000);
        }
        catch { }
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
                if (ocx.Connected > 0)
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

        int w = Math.Max(Width, MinDesktopWidth);
        int h = Math.Max(Height, MinDesktopHeight);
        if (w <= 0 || h <= 0) return;

        // Don't reconnect if size hasn't changed significantly (50px threshold)
        if (Math.Abs(w - _lastReconnectWidth) < 50 && Math.Abs(h - _lastReconnectHeight) < 50)
            return;

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
        DoResolutionReconnect();
    }

    private void DoResolutionReconnect()
    {
        if (_disposed || IsDisposed || _ocx is null || !_isConnected) return;
        if (_reconnectWidth <= 0 || _reconnectHeight <= 0) return;

        int w = _reconnectWidth;
        int h = _reconnectHeight;

        try
        {
            _reconnecting = true;

            dynamic ocx = _ocx;
            int connected = ocx.Connected;
            if (connected < 2) { _reconnecting = false; return; }

            Debug.WriteLine($"[RDP] Reconnect to {w}x{h}");
            ocx.Reconnect((uint)w, (uint)h);

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
// MsRdpClient10 exposes IMsRdpClientNonScriptable5 via QueryInterface
[ComImport, Guid("4F1F6ADA-0E89-4C70-A437-5398EB75A3AA")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IMsRdpClientNonScriptable5
{
    [DispId(4)] string ClearTextPassword { set; }
    // Other members omitted — we only need password
}
