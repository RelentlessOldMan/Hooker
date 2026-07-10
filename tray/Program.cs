// Hooker widget — a small always-on-top strip of per-session mascot tiles.
//
//   left-click a tile      : toggle that session's hooking (red <-> green)
//   drag a tile            : reorder it (works whether or not position is locked)
//   drag the grip (left)   : move the whole widget  (only when position is UNLOCKED)
//   right-click            : menu -> Lock position, new-session side, grow direction, Exit
//
// Each tile is colored from that session's .meta (green=working, yellow=waiting)
// unless the session is hooking, then red. Auto-approve is per session and lives
// entirely in the shim; this widget just writes each session's on/off .state.
//
// Growth: when tiles are added/removed the strip keeps one edge pinned. "Anchor"
// picks which edge (auto = whichever screen half the widget sits on) so a
// right-docked strip grows leftward and a left-docked one grows rightward.
//
// Position, lock, order, anchor, and new-session side persist to hooker\widget.json.
// The strip hides while a fullscreen app owns the same monitor (games safe).

using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HookerWidget;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "Hooker.Widget.SingleInstance", out bool isNew);
        if (!isNew) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new WidgetForm());
    }
}

sealed class Session
{
    public string Status = "working";   // working | waiting  -> background tint
    public string Cwd = "";
    public long Count;
    public bool Hooking;                 // salmon vs grey mascot
}

sealed class WidgetConfig
{
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public bool Locked { get; set; }
    public string Anchor { get; set; } = "auto";   // auto | left | right
    public string NewSide { get; set; } = "right";  // right | left
    public double StaleHours { get; set; } = 24;    // prune a tile after this long with no hook event (0 = never)
    public List<string> Order { get; set; } = new();
    public Dictionary<string, string> Labels { get; set; } = new();  // sid -> custom name
}

sealed class WidgetForm : Form
{
    const int Tile = 51, Gap = 7, Grip = 16, Pad = 7, Radius = 12, DragThreshold = 5;

    static readonly string HookerDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "hooker");
    static readonly string SessionsDir = Path.Combine(HookerDir, "sessions");
    static readonly string ConfigPath = Path.Combine(HookerDir, "widget.json");

    readonly Bitmap _workOn, _workOff, _waitOn, _waitOff;
    readonly System.Windows.Forms.Timer _poll;
    readonly TipWindow _tip = new();
    readonly ContextMenuStrip _menu = new();
    ToolStripMenuItem _lockItem = null!, _newRight = null!, _newLeft = null!,
                      _anchorAuto = null!, _anchorRight = null!, _anchorLeft = null!,
                      _newSideMenu = null!, _anchorMenu = null!, _exitItem = null!;

    readonly List<string> _order = new();
    readonly Dictionary<string, Session> _sessions = new();
    Dictionary<string, string> _labels = new();
    bool _locked;
    string _anchor = "auto";
    string _newSide = "right";
    double _staleHours = 4;

    enum Hit { None, Grip, Tile }
    Hit _hitKind;
    string? _dragSid;
    bool _moved;
    Point _downScreen, _downFormLoc;
    string _hoverSid = "", _hoverText = "";

    public WidgetForm()
    {
        _workOn = LoadPng("work_on.png");
        _workOff = LoadPng("work_off.png");
        _waitOn = LoadPng("wait_on.png");
        _waitOff = LoadPng("wait_off.png");

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(30, 30, 34);   // dark pill; rounded shape comes from Region (no key fringe)

        BuildMenu();
        LoadConfig();
        _ = Handle;                          // create handle so the timer pumps while hidden

        SyncSessions();
        InitialLayout();

        _poll = new System.Windows.Forms.Timer { Interval = 250 };
        _poll.Tick += (_, _) => Tick();
        _poll.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    static Bitmap LoadPng(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"missing resource {name}");
        return new Bitmap(s);
    }

    void BuildMenu()
    {
        _lockItem = new ToolStripMenuItem("Lock position", null, (_, _) => ToggleLock());

        _newRight = new ToolStripMenuItem("On the right", null, (_, _) => SetNewSide("right"));
        _newLeft = new ToolStripMenuItem("On the left", null, (_, _) => SetNewSide("left"));
        _newSideMenu = new ToolStripMenuItem("New sessions appear");
        _newSideMenu.DropDownItems.AddRange(new ToolStripItem[] { _newRight, _newLeft });

        _anchorAuto = new ToolStripMenuItem("Auto (by screen side)", null, (_, _) => SetAnchor("auto"));
        _anchorRight = new ToolStripMenuItem("Anchor right (grow left)", null, (_, _) => SetAnchor("right"));
        _anchorLeft = new ToolStripMenuItem("Anchor left (grow right)", null, (_, _) => SetAnchor("left"));
        _anchorMenu = new ToolStripMenuItem("Grow direction");
        _anchorMenu.DropDownItems.AddRange(new ToolStripItem[] { _anchorAuto, _anchorRight, _anchorLeft });

        _exitItem = new ToolStripMenuItem("Exit", null, (_, _) => Close());
    }

    // Assemble the right-click menu fresh each time so a per-tile "Dismiss" can be
    // shown only when a tile was clicked.
    void ShowMenu(Point p, string? tileSid)
    {
        _menu.Items.Clear();
        if (tileSid != null && _sessions.ContainsKey(tileSid))
        {
            _menu.Items.Add(new ToolStripMenuItem("Rename…", null, (_, _) => RenameSession(tileSid)));
            _menu.Items.Add(new ToolStripMenuItem($"Dismiss “{FolderOf(tileSid)}”",
                null, (_, _) => DismissSession(tileSid)));
            _menu.Items.Add(new ToolStripSeparator());
        }
        _menu.Items.Add(_lockItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_newSideMenu);
        _menu.Items.Add(_anchorMenu);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Reset position", null, (_, _) => ResetPosition()));
        _menu.Items.Add(_exitItem);
        _menu.Show(this, p);
    }

    string FolderOf(string sid) =>
        _sessions.TryGetValue(sid, out var s) && s.Cwd.Length > 0
            ? Path.GetFileName(s.Cwd.TrimEnd('/', '\\')) : "session";

    void DismissSession(string sid)
    {
        try { File.Delete(Path.Combine(SessionsDir, sid + ".meta")); } catch { }
        try { File.Delete(Path.Combine(SessionsDir, sid + ".state")); } catch { }
        _order.Remove(sid);
        _sessions.Remove(sid);
        _labels.Remove(sid);
        SaveConfig();
        Tick();
    }

    void RenameSession(string sid)
    {
        var current = _labels.GetValueOrDefault(sid, "");
        var name = Prompt("Name this session (blank to clear):", current);
        if (name == null) return;                     // cancelled
        if (name.Length == 0) _labels.Remove(sid);
        else _labels[sid] = name;
        SaveConfig();
        _hoverText = "";                              // force the tooltip to refresh
        Invalidate();
    }

    // Minimal modal text prompt (WinForms has no built-in InputBox).
    string? Prompt(string caption, string initial)
    {
        using var f = new Form
        {
            Text = "Rename",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(320, 96),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            TopMost = true,
        };
        var lbl = new Label { Left = 12, Top = 10, Width = 296, Text = caption };
        var tb = new TextBox { Left = 12, Top = 32, Width = 296, Text = initial };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 152, Top = 62, Width = 75 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 233, Top = 62, Width = 75 };
        f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        tb.SelectAll();
        return f.ShowDialog() == DialogResult.OK ? tb.Text.Trim() : null;
    }

    void RefreshMenuChecks()
    {
        _lockItem.Text = _locked ? "Unlock position" : "Lock position";
        _newRight.Checked = _newSide == "right";
        _newLeft.Checked = _newSide == "left";
        _anchorAuto.Checked = _anchor == "auto";
        _anchorRight.Checked = _anchor == "right";
        _anchorLeft.Checked = _anchor == "left";
    }

    // ---- layout ----------------------------------------------------------

    Size ContentSize()
    {
        int count = _order.Count;
        int w = count == 0
            ? Pad * 2 + Grip
            : Pad * 2 + Grip + Gap + count * Tile + (count - 1) * Gap;
        return new Size(w, Pad * 2 + Tile);
    }

    void InitialLayout()
    {
        Size = ContentSize();
        if (Location.X < 0 || Location.Y < 0) DockDefault();
        EnsureOnScreen();
        UpdateRegion();
        Visible = _order.Count > 0 && !ShouldHideForFullscreen();
        Invalidate();
    }

    void Tick()
    {
        SyncSessions();

        if (ShouldHideForFullscreen() || _order.Count == 0)
        {
            if (Visible) { Visible = false; _tip.HideTip(); _hoverSid = ""; _hoverText = ""; }
            return;
        }

        var want = ContentSize();
        if (want != Size)
        {
            bool anchorRight = EffectiveAnchorRight();   // decide from the pre-resize position
            int oldRight = Right;
            Size = want;
            // Height is constant with session count, so Y never changes here (no vertical
            // jump). Keep the anchored horizontal edge; only clamp X, and only when unlocked
            // so a locked widget stays exactly where you put it (even over the taskbar).
            if (anchorRight) Location = new Point(oldRight - want.Width, Location.Y);
            if (!_locked) ClampX();
            UpdateRegion();
        }
        if (!Visible) Visible = true;
        AssertTopmost();                                           // stay in front of the (also-topmost) taskbar
        if (!_moved) UpdateHover(PointToClient(Cursor.Position));   // keep tooltip in sync with live data
        Invalidate();
    }

    void ClampX()
    {
        var b = Screen.FromPoint(Location).Bounds;
        int x = Math.Clamp(Location.X, b.Left, Math.Max(b.Left, b.Right - Width));
        if (x != Location.X) Location = new Point(x, Location.Y);
    }

    void UpdateRegion()
    {
        var path = Rounded(new Rectangle(0, 0, Width, Height), Radius);
        var old = Region;
        Region = new Region(path);
        path.Dispose();
        old?.Dispose();
    }

    bool EffectiveAnchorRight()
    {
        if (_anchor == "right") return true;
        if (_anchor == "left") return false;
        var wa = Screen.FromRectangle(Bounds).WorkingArea;         // auto: which half are we on
        return Location.X + Width / 2 >= wa.Left + wa.Width / 2;
    }

    void DockDefault()
    {
        // Centered horizontally, just above the taskbar, on the monitor with the cursor.
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - 8);
    }

    void EnsureOnScreen()
    {
        // Full monitor bounds so it can sit on the taskbar (we keep it in front via
        // AssertTopmost); just don't let it wander off the physical screen.
        var b = Screen.FromPoint(Location).Bounds;
        int x = Math.Clamp(Location.X, b.Left, Math.Max(b.Left, b.Right - Width));
        int y = Math.Clamp(Location.Y, b.Top, Math.Max(b.Top, b.Bottom - Height));
        if (x != Location.X || y != Location.Y) Location = new Point(x, y);
    }

    void ResetPosition()
    {
        Size = ContentSize();
        DockDefault();               // centered, just above the taskbar, on the monitor you're on
        _locked = false;
        RefreshMenuChecks();
        UpdateRegion();
        SaveConfig();
        Invalidate();
    }

    // ---- data sync -------------------------------------------------------

    void SyncSessions()
    {
        List<string> live = new();
        try
        {
            Directory.CreateDirectory(SessionsDir);
            var cutoff = _staleHours > 0 ? DateTime.UtcNow.AddHours(-_staleHours) : DateTime.MinValue;
            foreach (var f in Directory.GetFiles(SessionsDir, "*.meta"))
            {
                var sid = Path.GetFileNameWithoutExtension(f);

                // Prune a tile whose session hasn't fired a hook event in StaleHours
                // (an abrupt close that skipped SessionEnd). Self-heals: if that
                // session is actually alive, its next event recreates the .meta.
                if (_staleHours > 0 && File.GetLastWriteTimeUtc(f) < cutoff)
                {
                    try { File.Delete(f); } catch { }
                    try { File.Delete(Path.Combine(SessionsDir, sid + ".state")); } catch { }
                    continue;
                }

                Session s = _sessions.TryGetValue(sid, out var existing) ? existing : new Session();
                try
                {
                    var m = JsonSerializer.Deserialize<MetaDto>(File.ReadAllText(f));
                    if (m != null) { s.Status = m.status; s.Cwd = m.cwd; s.Count = m.count; }
                }
                catch { }
                var statePath = Path.Combine(SessionsDir, sid + ".state");
                try { s.Hooking = File.Exists(statePath) && File.ReadAllText(statePath).Trim().Equals("on", StringComparison.OrdinalIgnoreCase); }
                catch { }
                _sessions[sid] = s;
                live.Add(sid);
            }
        }
        catch { }

        if (_moved && _hitKind == Hit.Tile) return;   // don't churn order mid reorder-drag

        bool changed = false;
        for (int i = _order.Count - 1; i >= 0; i--)
            if (!live.Contains(_order[i])) { _labels.Remove(_order[i]); _sessions.Remove(_order[i]); _order.RemoveAt(i); changed = true; }
        foreach (var sid in live)
            if (!_order.Contains(sid))
            {
                if (_newSide == "left") _order.Insert(0, sid); else _order.Add(sid);
                changed = true;
            }
        if (changed) SaveConfig();
    }

    sealed class MetaDto
    {
        public string status { get; set; } = "working";
        public string cwd { get; set; } = "";
        public long count { get; set; }
    }

    // ---- geometry --------------------------------------------------------

    Rectangle GripRect() => new(Pad, Pad, Grip, Tile);
    Rectangle TileRect(int i) => new(Pad + Grip + Gap + i * (Tile + Gap), Pad, Tile, Tile);

    Hit HitTest(Point p, out int index)
    {
        index = -1;
        if (GripRect().Contains(p)) return Hit.Grip;
        for (int i = 0; i < _order.Count; i++)
            if (TileRect(i).Contains(p)) { index = i; return Hit.Tile; }
        return Hit.None;
    }

    int SlotFromX(int x) =>
        Math.Clamp((int)Math.Round((x - (Pad + Grip + Gap)) / (double)(Tile + Gap)), 0, _order.Count - 1);

    // ---- painting --------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        g.Clear(Color.FromArgb(30, 30, 34));   // Region already clips to the rounded pill

        var gr = GripRect();
        using (var dot = new SolidBrush(Color.FromArgb(_locked ? 70 : 150, 200, 200, 205)))
            for (int cx = 0; cx < 2; cx++)
                for (int cy = 0; cy < 3; cy++)
                    g.FillEllipse(dot, gr.X + 3 + cx * 6, gr.Y + Tile / 2 - 11 + cy * 8, 4, 4);

        for (int i = 0; i < _order.Count; i++)
            g.DrawImage(TileFor(_sessions[_order[i]]), TileRect(i));
    }

    Bitmap TileFor(Session s) =>
        s.Status == "waiting" ? (s.Hooking ? _waitOn : _waitOff)
                              : (s.Hooking ? _workOn : _workOff);

    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---- interaction -----------------------------------------------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _tip.HideTip();        // don't let the tooltip sit over a tile you're clicking/dragging
        _hoverSid = "";
        _hoverText = "";
        if (e.Button == MouseButtons.Left)
        {
            _hitKind = HitTest(e.Location, out int idx);
            _dragSid = _hitKind == Hit.Tile ? _order[idx] : null;
            _moved = false;
            _downScreen = Cursor.Position;
            _downFormLoc = Location;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _hitKind != Hit.None)
        {
            var now = Cursor.Position;
            if (!_moved && (Math.Abs(now.X - _downScreen.X) > DragThreshold || Math.Abs(now.Y - _downScreen.Y) > DragThreshold))
                _moved = true;

            if (_moved)
            {
                if (_hitKind == Hit.Grip && !_locked)
                    Location = new Point(_downFormLoc.X + (now.X - _downScreen.X), _downFormLoc.Y + (now.Y - _downScreen.Y));
                else if (_hitKind == Hit.Tile && _dragSid != null)
                {
                    int target = SlotFromX(e.X), cur = _order.IndexOf(_dragSid);
                    if (target != cur && target >= 0)
                    {
                        _order.RemoveAt(cur);
                        _order.Insert(target, _dragSid);
                        Invalidate();
                    }
                }
            }
        }
        else UpdateHover(e.Location);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            var kind = HitTest(e.Location, out int idx);
            ShowMenu(e.Location, kind == Hit.Tile ? _order[idx] : null);
        }
        else if (e.Button == MouseButtons.Left)
        {
            if (!_moved && _hitKind == Hit.Tile && _dragSid != null) ToggleSession(_dragSid);
            else if (_moved)
            {
                if (_hitKind == Hit.Grip) EnsureOnScreen();   // snap back out of the taskbar
                SaveConfig();
            }
        }
        _hitKind = Hit.None;
        _dragSid = null;
        _moved = false;
        base.OnMouseUp(e);
    }

    void UpdateHover(Point p)
    {
        var kind = HitTest(p, out int idx);
        var sid = kind == Hit.Tile && _sessions.ContainsKey(_order[idx]) ? _order[idx] : "";
        var text = sid.Length > 0 ? TileTooltip(sid) : "";
        if (sid == _hoverSid && text == _hoverText) return;   // refresh when sid OR its data changes
        _hoverSid = sid;
        _hoverText = text;
        if (text.Length == 0) { _tip.HideTip(); return; }
        var tr = TileRect(idx);
        int anchorX = PointToScreen(new Point(tr.X + tr.Width / 2, tr.Y)).X;   // center of the hovered tile
        _tip.ShowTip(text, Bounds, anchorX);
    }

    string TileTooltip(string sid)
    {
        var s = _sessions[sid];
        var name = _labels.TryGetValue(sid, out var lbl) && lbl.Length > 0
            ? lbl
            : (s.Cwd.Length == 0 ? "session" : Path.GetFileName(s.Cwd.TrimEnd('/', '\\')));
        var enabled = s.Hooking ? "hooking" : "manual";
        var need = s.Status == "waiting" ? "waiting on you" : "working";
        return $"{name}\n{enabled} · {need}\n{s.Count} auto-approval{(s.Count == 1 ? "" : "s")}";
    }

    void ToggleSession(string sid)
    {
        if (!_sessions.TryGetValue(sid, out var s)) return;
        s.Hooking = !s.Hooking;
        try
        {
            Directory.CreateDirectory(SessionsDir);
            File.WriteAllText(Path.Combine(SessionsDir, sid + ".state"), s.Hooking ? "on" : "off");
        }
        catch { }
        Invalidate();
    }

    void ToggleLock() { _locked = !_locked; RefreshMenuChecks(); SaveConfig(); Invalidate(); }
    void SetNewSide(string v) { _newSide = v; RefreshMenuChecks(); SaveConfig(); }
    void SetAnchor(string v) { _anchor = v; RefreshMenuChecks(); SaveConfig(); }

    // ---- fullscreen guard ------------------------------------------------

    bool ShouldHideForFullscreen()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle) return false;

            var myMon = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
            var fgMon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
            if (myMon != fgMon) return false;

            if (!GetWindowRect(fg, out RECT wr)) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(fgMon, ref mi)) return false;

            return wr.left <= mi.rcMonitor.left && wr.top <= mi.rcMonitor.top &&
                   wr.right >= mi.rcMonitor.right && wr.bottom >= mi.rcMonitor.bottom;
        }
        catch { return false; }
    }

    // ---- persistence -----------------------------------------------------

    void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var c = JsonSerializer.Deserialize<WidgetConfig>(File.ReadAllText(ConfigPath));
                if (c != null)
                {
                    _locked = c.Locked;
                    _anchor = c.Anchor;
                    _newSide = c.NewSide;
                    _staleHours = c.StaleHours;
                    _order.AddRange(c.Order);
                    if (c.Labels != null) _labels = c.Labels;
                    if (c.X >= 0 && c.Y >= 0) Location = new Point(c.X, c.Y);
                }
            }
        }
        catch { }
        RefreshMenuChecks();
    }

    void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(HookerDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new WidgetConfig
            {
                X = Location.X, Y = Location.Y, Locked = _locked,
                Anchor = _anchor, NewSide = _newSide, StaleHours = _staleHours,
                Order = new(_order), Labels = new(_labels),
            }));
        }
        catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        foreach (var sid in _order)                 // never leave a session auto-approving
            try { File.WriteAllText(Path.Combine(SessionsDir, sid + ".state"), "off"); } catch { }
        SaveConfig();
        base.OnFormClosing(e);
    }

    // ---- Win32 -----------------------------------------------------------

    const uint MONITOR_DEFAULTTONEAREST = 2;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;

    void AssertTopmost()
    {
        try { SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER); }
        catch { }
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
}

// A small always-on-top tip we position ourselves, so it sits entirely ABOVE or
// BELOW the widget (never over the tiles) depending on where the widget is.
sealed class TipWindow : Form
{
    const int PadX = 9, PadY = 6, Gap = 8;
    readonly Label _lbl = new()
    {
        AutoSize = true,
        ForeColor = Color.White,
        BackColor = Color.Transparent,
        Location = new Point(PadX, PadY),
    };

    public TipWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 24, 28);
        Controls.Add(_lbl);
    }

    protected override bool ShowWithoutActivation => true;   // never steal focus

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void ShowTip(string text, Rectangle widget, int anchorCenterX)
    {
        _lbl.Text = text;
        var ps = _lbl.PreferredSize;
        Size = new Size(ps.Width + PadX * 2, ps.Height + PadY * 2);

        var scr = Screen.FromRectangle(widget);
        // Above the widget if it's in the bottom half of its monitor (the usual case,
        // sitting near the taskbar); otherwise below. Then keep it on the monitor.
        bool below = widget.Top < scr.WorkingArea.Top + scr.WorkingArea.Height / 2;
        int y = below ? widget.Bottom + Gap : widget.Top - Height - Gap;
        int x = Math.Clamp(anchorCenterX - Width / 2, scr.Bounds.Left, scr.Bounds.Right - Width);
        y = Math.Clamp(y, scr.Bounds.Top, scr.Bounds.Bottom - Height);
        Location = new Point(x, y);

        if (!Visible) Show();
    }

    public void HideTip() { if (Visible) Hide(); }
}
