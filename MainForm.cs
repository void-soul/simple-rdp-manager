namespace SimpleRdpManager;

public partial class MainForm : Form
{
    // --- State ---
    private List<ServerConfig> _servers = new();
    private Dictionary<int, RdpClientControl> _rdpControls = new(); // index -> control
    private int _zoomedIndex = -1;
    
    // --- UI Components ---
    private Panel _gridPanel = null!;
    private FlowLayoutPanel _toolbar = null!;
    private readonly Dictionary<int, Button> _serverButtons = new();
    private System.Windows.Forms.Timer _stateTimer = null!;
    private ContextMenuStrip _serverMenu = null!;
    
    // --- Colors ---
    private static readonly Color ToolbarBg = Color.FromArgb(40, 40, 40);
    private static readonly Color BtnNormal = Color.FromArgb(60, 60, 60);
    private static readonly Color BtnHover = Color.FromArgb(80, 80, 80);
    private static readonly Color BtnZoom = Color.FromArgb(0, 120, 215);
    private static readonly Color BtnConnected = Color.FromArgb(0, 160, 80);
    private static readonly Color BtnText = Color.FromArgb(230, 230, 230);
    private static readonly Color BtnAdd = Color.FromArgb(70, 130, 70);
    private static readonly Color BtnDel = Color.FromArgb(180, 60, 60);

    public MainForm()
    {
        Text = "简易宫格RDP管理器";
        Size = new Size(1600, 900);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(30, 30, 30);
        MinimumSize = new Size(800, 500);
        KeyPreview = true;
        
        InitializeUI();
        InitializeContextMenu();
        // Load config now, but defer RDP connections until OnLoad (form handle must be ready)
        _servers = ConfigManager.Load();
        CreateAllRdpControls(); // Creates controls but does not connect yet
        
        // State polling
        _stateTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _stateTimer.Tick += (s, e) =>
        {
            foreach (var ctrl in _rdpControls.Values)
                ctrl.RefreshConnectionState();
        };
        _stateTimer.Start();
        
        FormClosing += OnFormClosing;
        Resize += (s, e) => ArrangeGrid();
        Load += OnLoad;
    }

    private void OnLoad(object? sender, EventArgs e)
    {
        // Only auto-connect servers that have AutoConnect enabled
        foreach (var kvp in _rdpControls)
        {
            if (_servers[kvp.Key].AutoConnect)
                kvp.Value.ConnectWith(_servers[kvp.Key]);
        }
        RefreshToolbar();
        ArrangeGrid();
    }

    private void InitializeUI()
    {
        // Toolbar
        _toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 48,
            BackColor = ToolbarBg,
            Padding = new Padding(6, 6, 6, 6),
            AutoScroll = false,
            WrapContents = false
        };
        
        var addBtn = CreateToolButton("+ 添加", BtnAdd);
        addBtn.Click += (s, e) => ShowAddDialog();
        _toolbar.Controls.Add(addBtn);
        
        var delBtn = CreateToolButton("- 删除", BtnDel);
        delBtn.Click += (s, e) => ShowDeleteDialog();
        _toolbar.Controls.Add(delBtn);
        
        // Separator
        _toolbar.Controls.Add(new Label { Width = 20, Height = 36, BackColor = Color.Transparent });
        
        Controls.Add(_toolbar);
        
        // Grid panel
        _gridPanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(20, 20, 20)
        };
        Controls.Add(_gridPanel);
    }

    private Button CreateToolButton(string text, Color bgColor)
    {
        return new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            BackColor = bgColor,
            ForeColor = BtnText,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular),
            Height = 36,
            Width = 80,
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter,
            UseVisualStyleBackColor = false,
            Margin = new Padding(2)
        };
    }

    private Button CreateServerButton(int index, ServerConfig server)
    {
        var btn = new Button
        {
            Text = server.Name,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            BackColor = BtnNormal,
            ForeColor = BtnText,
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular),
            Height = 36,
            Width = 100,
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleCenter,
            UseVisualStyleBackColor = false,
            Margin = new Padding(2),
            Tag = index
        };
        
        btn.Click += (s, e) => OnServerButtonClick(index);
        btn.MouseEnter += (s, e) =>
        {
            if (index != _zoomedIndex)
                btn.BackColor = BtnHover;
        };
        btn.MouseLeave += (s, e) =>
        {
            if (index != _zoomedIndex)
            {
                var ctrl = _rdpControls.GetValueOrDefault(index);
                btn.BackColor = ctrl?.IsConnected == true ? BtnConnected : BtnNormal;
            }
        };
        btn.MouseDown += (s, e) =>
        {
            if (e.Button == MouseButtons.Right)
                ShowServerMenu(index, btn);
        };
        
        _serverButtons[index] = btn;
        return btn;
    }

    private void RefreshToolbar()
    {
        // Clear server buttons, keep add/del + separator
        while (_toolbar.Controls.Count > 3)
            _toolbar.Controls.RemoveAt(_toolbar.Controls.Count - 1);
        
        _serverButtons.Clear();
        
        for (int i = 0; i < _servers.Count; i++)
            _toolbar.Controls.Add(CreateServerButton(i, _servers[i]));
        
        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        for (int i = 0; i < _servers.Count; i++)
        {
            if (!_serverButtons.TryGetValue(i, out var btn)) continue;
            var ctrl = _rdpControls.GetValueOrDefault(i);
            
            if (i == _zoomedIndex)
                btn.BackColor = BtnZoom;
            else if (ctrl?.IsConnected == true)
                btn.BackColor = BtnConnected;
            else
                btn.BackColor = BtnNormal;
        }
    }

    // --- Server Button Click ---
    private void OnServerButtonClick(int index)
    {
        if (_zoomedIndex == index)
        {
            // Already zoomed: restore all
            _zoomedIndex = -1;
        }
        else
        {
            // Zoom this one
            _zoomedIndex = index;
        }
        
        UpdateButtonStates();
        ArrangeGrid();
    }

    // --- Grid Layout ---
    private void ArrangeGrid()
    {
        int n = _rdpControls.Count;
        if (n == 0) return;
        
        int pw = _gridPanel.Width;
        int ph = _gridPanel.Height;
        if (pw <= 0 || ph <= 0) return;
        
        if (_zoomedIndex >= 0 && _zoomedIndex < _servers.Count && _rdpControls.ContainsKey(_zoomedIndex) && n > 1)
        {
            ArrangeZoomed(pw, ph);
        }
        else
        {
            ArrangeNormal(pw, ph);
        }
    }

    private void ArrangeNormal(int pw, int ph)
    {
        int n = _rdpControls.Count;
        int cols = (int)Math.Ceiling(Math.Sqrt(n));
        int rows = (int)Math.Ceiling((double)n / cols);
        int cw = pw / cols;
        int ch = ph / rows;
        
        int idx = 0;
        foreach (var kvp in _rdpControls.OrderBy(k => k.Key))
        {
            int r = idx / cols;
            int c = idx % cols;
            
            // Last row might have fewer items, expand last item
            int span = 1;
            if (r == rows - 1)
            {
                int remaining = n - (rows - 1) * cols;
                if (remaining < cols && idx == (rows - 1) * cols + remaining - 1)
                    span = cols - remaining + 1;
            }
            
            kvp.Value.SetBounds(c * cw, r * ch, cw * span, ch);
            kvp.Value.UpdateDesktopScale();
            idx++;
        }
    }

    private void ArrangeZoomed(int pw, int ph)
    {
        int n = _rdpControls.Count;
        int nSmall = n - 1;
        
        // Sidebar width for small ones
        int sideW = Math.Max(pw / 4, 200);
        int mainW = pw - sideW - 2;
        
        // Big control
        if (_rdpControls.TryGetValue(_zoomedIndex, out var big))
        {
            big.SetBounds(0, 0, mainW, ph);
            big.UpdateDesktopScale();
        }
        
        // Small controls in sidebar
        int i = 0;
        int smallH = nSmall > 0 ? ph / nSmall : ph;
        
        foreach (var kvp in _rdpControls.OrderBy(k => k.Key))
        {
            if (kvp.Key == _zoomedIndex) continue;
            kvp.Value.SetBounds(mainW + 2, i * smallH, sideW - 2, smallH);
            kvp.Value.UpdateDesktopScale();
            i++;
        }
    }

    // --- Server Management ---
    private void LoadServers()
    {
        _servers = ConfigManager.Load();
        CreateAllRdpControls();
        RefreshToolbar();
        ArrangeGrid();
    }

    private void CreateAllRdpControls()
    {
        // Dispose old controls
        foreach (var ctrl in _rdpControls.Values)
        {
            ctrl.Disconnect();
            ctrl.Dispose();
        }
        _rdpControls.Clear();
        
        // Create new controls (connection deferred — call ConnectWith separately)
        for (int i = 0; i < _servers.Count; i++)
        {
            var ctrl = new RdpClientControl
            {
                Visible = true
            };
            ctrl.ConnectionStateChanged += OnConnectionStateChanged;
            int idx = i;
            ctrl.MouseClicked += (s, btn) =>
            {
                if (btn == MouseButtons.Right) ShowServerMenu(idx, ctrl);
                else if (btn == MouseButtons.Left) OnServerControlClick(idx);
            };
            
            _gridPanel.Controls.Add(ctrl);
            _rdpControls[i] = ctrl;
        }
    }

    private void OnServerControlClick(int index)
    {
        OnServerButtonClick(index);
    }

    private void OnConnectionStateChanged(RdpClientControl ctrl)
    {
        // Update button colors
        this.BeginInvoke(() => UpdateButtonStates());
    }

    private void ShowAddDialog()
    {
        using var dlg = new ServerEditDialog(_servers.Count, null);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Config != null)
        {
            _servers.Add(dlg.Config);
            ConfigManager.Save(_servers);
            
            // Create control for new server
            var ctrl = new RdpClientControl { Visible = true };
            ctrl.ConnectionStateChanged += OnConnectionStateChanged;
            ctrl.MouseClicked += (s, btn) =>
            {
                int idx = _servers.IndexOf(dlg.Config);
                if (btn == MouseButtons.Right) ShowServerMenu(idx, ctrl);
                else if (btn == MouseButtons.Left) OnServerControlClick(idx);
            };
            
            int newIdx = _servers.Count - 1;
            _gridPanel.Controls.Add(ctrl);
            _rdpControls[newIdx] = ctrl;
            ctrl.ConnectWith(dlg.Config);
            
            RefreshToolbar();
            ArrangeGrid();
        }
    }

    private void ShowDeleteDialog()
    {
        if (_servers.Count == 0)
        {
            MessageBox.Show("没有可删除的桌面。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        
        var names = _servers.Select(s => s.Name).ToArray();
        using var dlg = new DeleteDialog(names);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedIndex >= 0)
        {
            RemoveServer(dlg.SelectedIndex);
        }
    }

    private void RemoveServer(int index)
    {
        if (index < 0 || index >= _servers.Count) return;
        
        // Remove and dispose control
        if (_rdpControls.TryGetValue(index, out var ctrl))
        {
            _rdpControls.Remove(index);
            _gridPanel.Controls.Remove(ctrl);
            ctrl.Dispose(); // Dispose() internally handles Disconnect + COM cleanup
        }
        
        _servers.RemoveAt(index);
        
        // Re-map controls (shift indices)
        var newMap = new Dictionary<int, RdpClientControl>();
        foreach (var kvp in _rdpControls.OrderBy(k => k.Key))
        {
            int newIdx = kvp.Key > index ? kvp.Key - 1 : kvp.Key;
            newMap[newIdx] = kvp.Value;
        }
        _rdpControls = newMap;
        
        if (_zoomedIndex == index)
            _zoomedIndex = -1;
        else if (_zoomedIndex > index)
            _zoomedIndex--;
        
        ConfigManager.Save(_servers);
        RefreshToolbar();
        ArrangeGrid();
    }

    // --- Context Menu ---

    private int _contextMenuIndex = -1;

    private void InitializeContextMenu()
    {
        _serverMenu = new ContextMenuStrip
        {
            Renderer = new DarkToolStripRenderer(),
            BackColor = Color.FromArgb(50, 50, 53),
            ForeColor = Color.FromArgb(220, 220, 220)
        };

        var connectItem = new ToolStripMenuItem("重新连接");
        connectItem.Click += (s, e) => ConnectServer(_contextMenuIndex);
        _serverMenu.Items.Add(connectItem);

        var disconnectItem = new ToolStripMenuItem("断开连接");
        disconnectItem.Click += (s, e) => DisconnectServer(_contextMenuIndex);
        _serverMenu.Items.Add(disconnectItem);

        _serverMenu.Items.Add(new ToolStripSeparator());

        var settingsItem = new ToolStripMenuItem("设置...");
        settingsItem.Click += (s, e) => ShowServerSettings(_contextMenuIndex);
        _serverMenu.Items.Add(settingsItem);
    }

    private void ShowServerMenu(int index, Control source)
    {
        if (index < 0 || index >= _servers.Count) return;
        _contextMenuIndex = index;
        _serverMenu.Show(source, new Point(0, source.Height));
    }

    private void DisconnectServer(int index)
    {
        if (index < 0 || index >= _servers.Count) return;

        if (_rdpControls.TryGetValue(index, out var ctrl))
        {
            ctrl.Disconnect();
        }

        // Reset zoom if the disconnected server was zoomed
        if (_zoomedIndex == index)
            _zoomedIndex = -1;

        UpdateButtonStates();
        ArrangeGrid();
    }

    private void ConnectServer(int index)
    {
        if (index < 0 || index >= _servers.Count) return;

        if (_rdpControls.TryGetValue(index, out var ctrl))
        {
            ctrl.ConnectWith(_servers[index]);
        }

        UpdateButtonStates();
        ArrangeGrid();
    }

    private void ShowServerSettings(int index)
    {
        if (index < 0 || index >= _servers.Count) return;
        
        var clone = CloneConfig(_servers[index]);
        using var dlg = new ServerEditDialog(index, clone);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Config != null)
        {
            // Update config
            _servers[index] = dlg.Config;
            ConfigManager.Save(_servers);
            
            // Reconnect with new settings
            if (_rdpControls.TryGetValue(index, out var ctrl))
            {
                ctrl.Disconnect();
                ctrl.ConnectWith(_servers[index]);
            }
            
            RefreshToolbar();
            ArrangeGrid();
        }
    }

    private static ServerConfig CloneConfig(ServerConfig src)
    {
        return new ServerConfig
        {
            Name = src.Name,
            Ip = src.Ip,
            Port = src.Port,
            UserName = src.UserName,
            Password = src.Password,
            DesktopScale = src.DesktopScale,
            ColorDepth = src.ColorDepth,
            AutoConnect = src.AutoConnect
        };
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // Disconnect all
        foreach (var ctrl in _rdpControls.Values)
            ctrl.Disconnect();
        _stateTimer.Stop();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F11)
        {
            ToggleFullScreen();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ToggleFullScreen()
    {
        if (FormBorderStyle == FormBorderStyle.None)
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = FormWindowState.Normal;
            _toolbar.Visible = true;
        }
        else
        {
            _toolbar.Visible = false; // auto-hide toolbar in fullscreen
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            // Show toolbar on mouse move to top
            this.MouseMove += ShowToolbarOnHover;
        }
    }

    private void ShowToolbarOnHover(object? sender, MouseEventArgs e)
    {
        if (e.Y < 10)
            _toolbar.Visible = true;
        else if (e.Y > 60)
            _toolbar.Visible = false;
    }
}

// =============== Dialogs ===============

public class ServerEditDialog : Form
{
    public ServerConfig? Config { get; private set; }
    
    private TextBox _nameBox = null!;
    private TextBox _ipBox = null!;
    private NumericUpDown _portBox = null!;
    private TextBox _userBox = null!;
    private TextBox _passBox = null!;
    private ComboBox _scaleBox = null!;
    private ComboBox _colorBox = null!;
    private CheckBox _autoCb = null!;
    private int _index;

    public ServerEditDialog(int index, ServerConfig? existing)
    {
        _index = index;
        Text = existing == null ? "添加远程桌面" : $"编辑 - {existing.Name}";
        Size = new Size(420, 440);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(45, 45, 48);
        ForeColor = Color.FromArgb(220, 220, 220);
        
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 15, 20, 15),
            ColumnCount = 2,
            RowCount = 10,
            ColumnStyles = { new ColumnStyle(SizeType.Absolute, 100), new ColumnStyle(SizeType.Percent, 100) }
        };
        
        for (int i = 0; i < 10; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        int row = 0;
        AddField(layout, ref row, "名称:", _nameBox = new TextBox { Text = existing?.Name ?? $"Server{_index + 1}" });
        AddField(layout, ref row, "IP地址:", _ipBox = new TextBox { Text = existing?.Ip ?? "192.168.1.100" });
        
        _portBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = existing?.Port ?? 3389 };
        AddField(layout, ref row, "端口:", _portBox);
        
        AddField(layout, ref row, "用户名:", _userBox = new TextBox { Text = existing?.UserName ?? "Administrator" });
        
        _passBox = new TextBox { Text = existing?.GetPassword() ?? "", UseSystemPasswordChar = true };
        AddField(layout, ref row, "密码:", _passBox);
        
        _scaleBox = new ComboBox 
        { 
            DropDownStyle = ComboBoxStyle.DropDownList,
            Items = { "100%", "125%", "150%", "175%", "200%" },
            Text = existing?.DesktopScale > 0 ? $"{existing.DesktopScale}%" : "100%"
        };
        AddField(layout, ref row, "缩放:", _scaleBox);
        
        _colorBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Items = { "32位", "24位", "16位", "15位" },
            Text = existing?.ColorDepth switch { 24 => "24位", 16 => "16位", 15 => "15位", _ => "32位" }
        };
        AddField(layout, ref row, "色彩:", _colorBox);
        
        // Auto-connect checkbox
        _autoCb = new CheckBox
        {
            Text = "启动时自动连接",
            Checked = existing?.AutoConnect ?? true,
            ForeColor = Color.FromArgb(200, 200, 200),
            Font = new Font("Microsoft YaHei UI", 10f),
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(45, 45, 48),
            FlatStyle = FlatStyle.Flat
        };
        layout.Controls.Add(_autoCb, 1, row);
        row++;
        
        layout.RowStyles[row-1] = new RowStyle(SizeType.Absolute, 50);
        
        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0)
        };
        
        var cancelBtn = CreateDialogButton("取消", Color.FromArgb(80, 80, 80));
        cancelBtn.Click += (s, e) => { Config = null; DialogResult = DialogResult.Cancel; Close(); };
        btnPanel.Controls.Add(cancelBtn);
        
        var saveBtn = CreateDialogButton("保存", Color.FromArgb(0, 120, 215));
        saveBtn.Click += (s, e) => Save();
        saveBtn.Margin = new Padding(0, 0, 10, 0);
        btnPanel.Controls.Add(saveBtn);
        
        layout.Controls.Add(btnPanel, 1, row - 1);
        
        Controls.Add(layout);
        AcceptButton = saveBtn;
        CancelButton = cancelBtn;
    }

    private void Save()
    {
        var name = _nameBox.Text.Trim();
        var ip = _ipBox.Text.Trim();
        
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(ip))
        {
            MessageBox.Show("名称和IP地址不能为空。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        
        Config = new ServerConfig
        {
            Name = name,
            Ip = ip,
            Port = (int)_portBox.Value,
            UserName = _userBox.Text.Trim(),
            DesktopScale = int.Parse(_scaleBox.Text.Replace("%", "")),
            ColorDepth = _colorBox.Text switch 
            { 
                "24位" => 24, "16位" => 16, "15位" => 15, _ => 32 
            },
            AutoConnect = _autoCb.Checked
        };
        Config.SetPassword(_passBox.Text);
        
        DialogResult = DialogResult.OK;
        Close();
    }

    private void AddField(TableLayoutPanel layout, ref int row, string label, Control ctrl)
    {
        var lbl = new Label
        {
            Text = label,
            TextAlign = ContentAlignment.MiddleRight,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(200, 200, 200),
            Font = new Font("Microsoft YaHei UI", 10f)
        };
        layout.Controls.Add(lbl, 0, row);
        
        ctrl.Dock = DockStyle.Fill;
        ctrl.Font = new Font("Microsoft YaHei UI", 10f);
        ctrl.BackColor = Color.FromArgb(60, 60, 63);
        ctrl.ForeColor = Color.FromArgb(220, 220, 220);
        layout.Controls.Add(ctrl, 1, row);
        
        row++;
    }

    private Button CreateDialogButton(string text, Color bg)
    {
        return new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0 },
            BackColor = bg,
            ForeColor = Color.FromArgb(230, 230, 230),
            Font = new Font("Microsoft YaHei UI", 10f),
            Size = new Size(80, 32),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
    }
}

public class DeleteDialog : Form
{
    public int SelectedIndex { get; private set; } = -1;
    
    public DeleteDialog(string[] names)
    {
        Text = "选择要删除的桌面";
        Size = new Size(300, 200);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(45, 45, 48);
        ForeColor = Color.FromArgb(220, 220, 220);
        
        var listBox = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(60, 60, 63),
            ForeColor = Color.FromArgb(220, 220, 220),
            Font = new Font("Microsoft YaHei UI", 10f),
            BorderStyle = BorderStyle.None
        };
        listBox.Items.AddRange(names);
        listBox.SelectedIndexChanged += (s, e) => SelectedIndex = listBox.SelectedIndex;
        Controls.Add(listBox);
        
        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            BackColor = Color.FromArgb(50, 50, 53),
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 8, 10, 0)
        };
        
        var cancelBtn = new Button
        {
            Text = "取消", FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0 },
            BackColor = Color.FromArgb(80, 80, 80), ForeColor = Color.FromArgb(220, 220, 220),
            Size = new Size(70, 32), Cursor = Cursors.Hand, UseVisualStyleBackColor = false
        };
        cancelBtn.Click += (s, e) => { SelectedIndex = -1; DialogResult = DialogResult.Cancel; Close(); };
        btnPanel.Controls.Add(cancelBtn);
        
        var delBtn = new Button
        {
            Text = "删除", FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0 },
            BackColor = Color.FromArgb(180, 60, 60), ForeColor = Color.FromArgb(230, 230, 230),
            Size = new Size(70, 32), Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
            Margin = new Padding(0, 0, 10, 0)
        };
        delBtn.Click += (s, e) =>
        {
            if (SelectedIndex < 0)
                MessageBox.Show("请先选择一个桌面。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        };
        btnPanel.Controls.Add(delBtn);
        
        Controls.Add(btnPanel);
    }
}

// ── Dark-themed ToolStrip renderer ─────────────────────

public class DarkToolStripRenderer : ToolStripProfessionalRenderer
{
    public DarkToolStripRenderer() : base(new DarkColorTable()) { }

    private class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Color.FromArgb(0, 120, 215);
        public override Color MenuItemBorder => Color.FromArgb(50, 50, 53);
        public override Color MenuBorder => Color.FromArgb(60, 60, 63);
        public override Color ToolStripDropDownBackground => Color.FromArgb(50, 50, 53);
        public override Color ImageMarginGradientBegin => Color.FromArgb(50, 50, 53);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(50, 50, 53);
        public override Color ImageMarginGradientEnd => Color.FromArgb(50, 50, 53);
        public override Color SeparatorDark => Color.FromArgb(80, 80, 80);
        public override Color SeparatorLight => Color.FromArgb(80, 80, 80);
        public override Color MenuItemPressedGradientBegin => Color.FromArgb(0, 100, 180);
        public override Color MenuItemPressedGradientEnd => Color.FromArgb(0, 100, 180);
    }
}
