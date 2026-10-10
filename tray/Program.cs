// Hooker widget — a small always-on-top strip of per-session mascot tiles.
//
//   left-click a tile      : toggle that session's hooking (salmon = auto, grey = manual)
//   drag a tile            : reorder it (works whether or not position is locked)
//   drag the grip (left)   : move the whole widget  (only when position is UNLOCKED)
//   right-click            : menu -> Dismiss, Lock position, System meter, new-session side, grow dir, Exit
//
// Two axes per tile: background = needs-you (green = waiting on you, yellow = working),
// mascot = enabled (salmon = hooking/auto-approve, grey = manual). Auto-approve is per
// session and lives entirely in the shim; this widget just writes each session's on/off
// .state and reads Claude's session registry (which sessions exist, /name, live busy)
// plus each session's hook-written .meta (auto-approval tally) to render.
//
// A tile is one running session: "<sid>@<pid>", the session id plus its Claude process. Two
// windows that resume the same conversation share a session id but are two sessions, so they
// get two tiles, each with its own autopilot. (Just "<sid>" when there's no registry to say
// which process.) Remember autopilot gives each window back its own setting; a window opened
// since (a resume) gets its conversation's.
//
// System meter (toggle in the right-click menu): the left-most "two tiles" are live vertical
// bars (no labels) of Task Manager's Performance numbers - CPU, Memory, Network, GPU - with detail
// (speeds, cores, memory, GPU temp...) on hover. It's fixed in place: always first, never
// draggable, never a drop target. Sampling lives in SystemMeter.cs.
//
// Growth: when tiles are added/removed the strip keeps one edge pinned. "Anchor"
// picks which edge (auto = whichever screen half the widget sits on) so a
// right-docked strip grows leftward and a left-docked one grows rightward.
//
// Position, lock, order, anchor, and new-session side persist to hooker\widget.json.
// The strip hides while a fullscreen app owns the same monitor (games safe).

using Microsoft.Win32;
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
        // First thing once we hold the mutex (the shim's "widget is running" signal): start every
        // session manual. Any later and a tool call landing during start-up could be auto-approved
        // by a stale "on" left behind by a crash.
        WidgetForm.WipeStates();
        ApplicationConfiguration.Initialize();
        // A transient WinForms hiccup should never kill an always-on widget.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, _) => { };
        Application.Run(new WidgetForm());
    }
}

sealed class Session
{
    public string Status = "working";   // hook-derived: working | waiting
    public string Cwd = "";
    public long Count;
    public bool Hooking;                 // salmon vs grey mascot
    public string Name = "";             // /name title, from Claude's session registry
    public string LiveStatus = "";       // Claude's own status for the session ("busy"/"idle"/...), "" if not in registry
    public long StartedAt;               // registry startedAt (ms); 0 if unknown. Orders same-name tiles.

    // Claude's live registry status is authoritative when present: it's updated
    // continuously, so "idle" reliably clears once a turn ends. Our hook .meta is only
    // edge-triggered (working on activity, waiting on Stop) and gets stuck at "working"
    // if a closing Stop never fires — so trust it ONLY as a fallback when the registry
    // is silent about this session (undocumented API absent/changed).
    public bool Working => LiveStatus.Length > 0 ? LiveStatus == "busy"
                                                 : Status == "working";
}

sealed class WidgetConfig
{
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public bool Locked { get; set; }
    public string Anchor { get; set; } = "auto";   // auto | left | right  (grow direction)
    public string NewSide { get; set; } = "right";  // right | left
    public List<string> Order { get; set; } = new();

    // Home as an edge-relative spec so it can be reproduced on a different-size/DPI screen (a
    // smaller RDP session) instead of hard-clamping to the edge. GapX/GapY are logical px from
    // the anchored corner; -1 = unset (legacy config, derived on first run on the home screen).
    public bool HomeRight { get; set; }
    public bool HomeBottom { get; set; } = true;
    public double HomeGapX { get; set; } = -1;
    public double HomeGapY { get; set; } = -1;

    // Sessions you dismissed that are still live: without this a restart would bring every
    // dismissed tile straight back, since tiles are built from Claude's session registry.
    public List<string> Dismissed { get; set; } = new();

    public bool Meter { get; set; } = true;   // show the system meter (on unless you turn it off)

    // Opt-in "Remember autopilot": sessions you put on autopilot, switched back on whenever they
    // reappear (widget restart/update, `claude -r`). sid -> when last confirmed on (unix ms), so
    // long-gone sessions age out. Off by default: without it every start/resume is manual.
    public bool RememberAutopilot { get; set; }
    public Dictionary<string, long> Autopilot { get; set; } = new();
    public List<string> OnTiles { get; set; } = new();

    // Strip width when X/Y was captured, in logical (DPI-independent) px; 0 = legacy/unknown.
    // X/Y is a top-left corner, so if the strip comes back a different width (sessions came/went,
    // meter toggled) a right-anchored strip must shift by the difference to keep its RIGHT edge.
    public double HomeW { get; set; }

    // Whether X/Y holds a real spot. A flag, not a sign test: a monitor left of / above the
    // primary has negative coordinates, which are perfectly valid homes.
    public bool HomeSet { get; set; }
}

sealed class WidgetForm : Form
{
    // Sizes were hand-tuned on the author's 4K @ 200% console (192 dpi). To hold a constant
    // apparent size elsewhere — notably a lower-DPI Remote Desktop session, where fixed raw
    // pixels ballooned relative to that session's 100% UI — every size scales by
    // DeviceDpi/RefDpi. On the 200% console the factor is exactly 1, so the widget stays
    // pixel-identical to how it was tuned; a 100% RDP view renders at half.
    const int RefDpi = 192;
    const int BaseTile = 51, BaseGap = 7, BaseGrip = 16, BasePad = 7, BaseRadius = 12, DragThreshold = 5;
    const int RegMissesToPrune = 15;   // ticks a session may be registry-absent (poll=100ms => ~1.5s) before its tile is evicted

    double _scale = 1.0;               // DeviceDpi / RefDpi; refreshed on DPI/display changes
    int Sc(int v) => (int)Math.Round(v * _scale);
    int Tile => Sc(BaseTile);
    int Gap => Sc(BaseGap);
    int Grip => Sc(BaseGrip);
    int Pad => Sc(BasePad);
    int Radius => Sc(BaseRadius);

    static readonly string HookerDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "hooker");
    static readonly string SessionsDir = Path.Combine(HookerDir, "sessions");
    static readonly string ConfigPath = Path.Combine(HookerDir, "widget.json");

    readonly Bitmap _workOn, _workOff, _waitOn, _waitOff;
    readonly string _version;
    readonly System.Windows.Forms.Timer _poll;

    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);
    readonly WinEventProc _winEventProc;
    IntPtr _winEventHook;
    readonly TipWindow _tip = new();
    readonly ContextMenuStrip _menu = new();
    ToolStripMenuItem _lockItem = null!, _newRight = null!, _newLeft = null!,
                      _anchorAuto = null!, _anchorRight = null!, _anchorLeft = null!,
                      _newSideMenu = null!, _anchorMenu = null!, _exitItem = null!, _meterItem = null!, _rememberItem = null!;

    readonly List<string> _order = new();
    readonly Dictionary<string, Session> _sessions = new();
    readonly HashSet<string> _seenInReg = new(StringComparer.OrdinalIgnoreCase);  // sids Claude has registered this run
    readonly Dictionary<string, int> _regMiss = new();                            // consecutive ticks a seen sid is registry-absent
    readonly HashSet<string> _dismissed = new(StringComparer.OrdinalIgnoreCase);  // tiles you dismissed; suppressed until they act again or die
    bool _configDirty;                                                            // sync changed something persisted: save once
    readonly HashSet<string> _lastOn = new(StringComparer.OrdinalIgnoreCase);     // tiles whose switch was last seen "on" (persisted)
    readonly HashSet<string> _knownTiles = new(StringComparer.OrdinalIgnoreCase); // tiles that were already up when the widget started
    readonly HashSet<string> _rememberedAtStart = new(StringComparer.OrdinalIgnoreCase);   // new tiles whose conversation was remembered as they appeared
    readonly Dictionary<string, bool> _userSet = new(StringComparer.OrdinalIgnoreCase);   // tiles you clicked or dismissed this run -> on? (Remember keeps that)
    readonly Dictionary<(string File, long ProcStart), string> _fileSid = new();  // registry entry -> the tile it lists
    readonly Dictionary<(string File, long ProcStart), int> _fileMiss = new();    // consecutive ticks a tracked entry went unread
    readonly Dictionary<long, (string Name, long Created)> _pidSeen = new();       // what each registered pid was when first seen
    readonly Dictionary<long, long> _deadAt = new();                               // pid seen dead -> its entry's last write then
    readonly Dictionary<string, (long At, bool WasOn)> _swapped = new(StringComparer.OrdinalIgnoreCase);   // new tile <- same process swapped ids
    readonly Dictionary<string, long> _tileSince = new(StringComparer.OrdinalIgnoreCase);   // when each tile appeared (Remember restores only then)
    int _stateSweep;                                                              // throttles the stale-.state sweep
    bool _meterOn = true;                       // the left "two-tile" system meter
    bool _remember;                             // "Remember autopilot" (opt-in)
    readonly Dictionary<string, long> _remembered = new(StringComparer.OrdinalIgnoreCase);   // session ids (not tiles) to switch back on
    const long RememberDays = 90;               // forget a session not seen on autopilot for this long
    readonly SystemMeter _meter = new();
    bool _locked;
    string _anchor = "auto";
    string _newSide = "right";
    Point _home = new(-1, -1);   // the spot you chose (persisted); we always return here, and only
                                 // ever clamp *off* it temporarily to stay visible — never saving that.
    bool _homeSet;               // _home holds a real spot (coordinates may be negative on a left/upper monitor)
    double _homeW;               // logical strip width when _home was captured; see RebaseHome
    bool _relValid;              // edge-relative spec of _home captured (which corner + logical gap)
    bool _relRight, _relBottom = true;
    double _relGapX, _relGapY;
    int _tick;
    string _sig = "";
    string _screenSig = "";   // monitor layout + DPI; a change means a topology/resolution/RDP switch

    // In-memory diagnostic ring buffer (no disk writes until you right-click -> Save debug log).
    // Captures startup + every display/DPI/topology change — the data needed to debug positioning.
    const int LogCap = 400;
    readonly Queue<string> _log = new();

    enum Hit { None, Grip, Meter, Tile }
    Hit _hitKind;
    string? _dragSid;
    bool _moved;
    Point _downScreen, _downFormLoc;
    int _dragGrabDX;             // cursor-to-tile-left offset at grab, so a lifted tile tracks under the pointer
    Point _dragPos;              // current pointer, form coords (only meaningful mid tile-drag)
    string _hoverSid = "", _hoverText = "";

    public WidgetForm()
    {
        _workOn = LoadPng("work_on.png");
        _workOff = LoadPng("work_off.png");
        _waitOn = LoadPng("wait_on.png");
        _waitOff = LoadPng("wait_off.png");

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        _version = v is null ? "" : $"v{v.Major}.{v.Minor}.{v.Build}";

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        // Fixed-pixel, hand-drawn widget: never let WinForms auto-rescale us when we land on
        // a different-DPI monitor (PerMonitorV2). We own our size (ContentSize) and react to
        // DPI/topology changes ourselves in ReconcileDisplay — auto-scaling would fight that.
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(30, 30, 34);   // dark pill; rounded shape comes from Region (no key fringe)

        BuildMenu();
        LoadConfig();
        _ = Handle;                          // create handle so the timer pumps while hidden
        UpdateScale();                       // adopt the starting monitor's DPI before first layout
        try { Directory.CreateDirectory(SessionsDir); } catch { }   // once, not every tick

        // Autopilot never outlives the widget instance that granted it. OnFormClosing turns every
        // session off, but a crash, a Task Manager kill or a release restart skips that - so
        // start every session manual, whatever earlier runs left behind. (The shim also refuses
        // to auto-approve while no widget is running; this covers the restart.)
        WipeStates();   // Main already did this first thing; again in case a file was locked then

        SyncSessions();
        InitialLayout();

        _poll = new System.Windows.Forms.Timer { Interval = 100 };
        _poll.Tick += (_, _) => Tick();
        _poll.Start();

        // The meter samples on a thread-pool timer; each fresh sample just repaints its tile.
        _meter.Sampled += () =>
        {
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(new Action(() => { if (_meterOn && Visible) Invalidate(MeterRect()); }));
            }
            catch { }   // racing shutdown
        };
        if (_meterOn) _meter.Start();

        // Re-assert topmost the instant the foreground window changes (e.g. pressing Win
        // raises the taskbar/Start), so the shell can't jump in front of us.
        _winEventProc = OnForegroundChanged;
        _winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);

        // Resolution/topology changes (duplicate toggled, a monitor of a different size or
        // DPI added/removed) fire this — reconcile so we can never end up off-screen,
        // wrong-sized, or growing the wrong way. WM_DISPLAYCHANGE/WM_DPICHANGED (WndProc)
        // cover the cases this doesn't.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        Log($"START {_version} refDpi={RefDpi} :: {StateSnapshot()}");
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

    protected override void WndProc(ref Message m)
    {
        const int WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0;
        base.WndProc(ref m);
        // Belt-and-suspenders alongside SystemEvents.DisplaySettingsChanged: a resolution
        // change (WM_DISPLAYCHANGE) or a move to a different-DPI monitor (WM_DPICHANGED, which
        // PerMonitorV2 delivers) both land here even for this WS_EX_NOACTIVATE tool window.
        if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_DPICHANGED)
            ReconcileDisplay(m.Msg == WM_DPICHANGED ? "wm_dpichanged" : "wm_displaychange");
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

        _meterItem = new ToolStripMenuItem("System meter", null, (_, _) => ToggleMeter());
        _rememberItem = new ToolStripMenuItem("Remember autopilot", null, (_, _) => ToggleRemember())
        {
            ToolTipText = "Sessions you put on autopilot switch back on when they return\n(widget restart, claude -r). New sessions still start manual.",
        };
        _exitItem = new ToolStripMenuItem("Exit", null, (_, _) => Close());
    }

    // Assemble the right-click menu fresh each time so a per-tile "Dismiss" can be
    // shown only when a tile was clicked.
    void ShowMenu(Point p, string? tileSid)
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem($"Hooker {_version}") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());
        if (tileSid != null && _sessions.ContainsKey(tileSid))
        {
            _menu.Items.Add(new ToolStripMenuItem($"Dismiss “{Label(tileSid)}”",
                null, (_, _) => DismissSession(tileSid)));
            _menu.Items.Add(new ToolStripSeparator());
        }
        _menu.Items.Add(_lockItem);
        _menu.Items.Add(_meterItem);
        _menu.Items.Add(_rememberItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_newSideMenu);
        _menu.Items.Add(_anchorMenu);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Reset position", null, (_, _) => ResetPosition()));
        _menu.Items.Add(new ToolStripMenuItem("Save debug log", null, (_, _) => SaveDebugLog()));
        _menu.Items.Add(_exitItem);
        SetForegroundWindow(Handle);   // lets the menu dismiss on ANY outside click, not just the grip
        _menu.Show(this, p);
    }

    // What a tile is called. Two sessions in the SAME folder with no /name render the same
    // DisplayName, which makes their tiles impossible to tell apart - so when labels clash we
    // number them by launch order: (1) is the one you started first. Giving a session a /name
    // is still the better fix; this just keeps the folder fallback honest.
    string Label(string sid)
    {
        if (!_sessions.TryGetValue(sid, out var s)) return "session";
        var name = DisplayName(s);
        int clashes = 0, rank = 1;
        foreach (var o in _order)
        {
            if (o == sid || !_sessions.TryGetValue(o, out var t) || DisplayName(t) != name) continue;
            clashes++;
            // Tie-break on sid as well so the numbering can't flip between ticks.
            if (t.StartedAt < s.StartedAt || (t.StartedAt == s.StartedAt && string.CompareOrdinal(o, sid) < 0)) rank++;
        }
        return clashes == 0 ? name : $"{name} ({rank})";
    }

    void DeleteSessionFiles(string sid)
    {
        try { File.Delete(Path.Combine(SessionsDir, sid + ".meta")); } catch { }
        try { File.Delete(Path.Combine(SessionsDir, sid + ".state")); } catch { }
    }

    void DismissSession(string sid)
    {
        _dismissed.Add(sid);   // the session is still live, so without this the tile returns next tick
        _userSet[sid] = false; // and when it does, it's manual: Remember mustn't switch it back on
        DeleteSessionFiles(sid);
        _lastOn.Remove(sid);
        ForgetUnlessOn(sid);       // hiding a tile ends its autopilot for good - it comes back manual
        _regMiss.Remove(sid);
        _order.Remove(sid);
        _sessions.Remove(sid);
        SaveConfig();
        Tick();
    }

    void RefreshMenuChecks()
    {
        _lockItem.Text = _locked ? "Unlock position" : "Lock position";
        _meterItem.Checked = _meterOn;
        _rememberItem.Checked = _remember;
        _newRight.Checked = _newSide == "right";
        _newLeft.Checked = _newSide == "left";
        _anchorAuto.Checked = _anchor == "auto";
        _anchorRight.Checked = _anchor == "right";
        _anchorLeft.Checked = _anchor == "left";
    }

    // ---- layout ----------------------------------------------------------

    Size ContentSize()
    {
        // The meter counts as two tile slots, so it's exactly two tiles + the gap between them.
        int slots = _order.Count + (_meterOn ? MeterSlots : 0);
        int w = slots == 0
            ? Pad * 2 + Grip
            : Pad * 2 + Grip + Gap + slots * Tile + (slots - 1) * Gap;
        return new Size(w, Pad * 2 + Tile);
    }

    void InitialLayout()
    {
        Size = ContentSize();
        if (!_homeSet) DockDefault();        // first run / no saved spot: centered above the taskbar
        RebaseHome();                        // came back a different width than when home was saved
        // Started on a screen home doesn't fit (inside an RDP session): same corner + gap as home,
        // exactly as ReconcileDisplay would place us, rather than a hard clamp to the edge.
        Location = !HomeFitsSomeScreen() && _relValid ? HomeFromRel() : _home;
        EnsureOnScreen();                    // clamp only the live position; _home is the truth
        if (HomeFitsSomeScreen()) ComputeRel();   // capture/refresh the edge-relative spec (also migrates legacy configs)
        UpdateRegion();
        _screenSig = ScreenSig();            // baseline; Tick reconciles when this later changes
        Visible = (_order.Count > 0 || _meterOn) && !ShouldHideForFullscreen();
        Invalidate();
    }

    void Tick()
    {
        try
        {
            SyncSessions();

            // Poll the display topology: RDP connect/disconnect and resolution changes don't
            // reliably deliver WM_DISPLAYCHANGE/DisplaySettingsChanged to this always-on tool
            // window, so the event-driven ReconcileDisplay can miss them and leave us stranded
            // where the *other* screen put us. Catching the change here snaps us back to _home.
            var scr = ScreenSig();
            // Defer while dragging: don't consume the change (leave _screenSig stale) so the next
            // idle tick reconciles once the drag ends, instead of snapping home mid-drag.
            if (scr != _screenSig && !_moved) { _screenSig = scr; ReconcileDisplay("topology-poll"); }

            if (ShouldHideForFullscreen() || (_order.Count == 0 && !_meterOn))
            {
                if (Visible) { Visible = false; _tip.HideTip(); _hoverSid = ""; _hoverText = ""; }
                _meter.Paused = true;   // nobody can see it (e.g. a fullscreen game) - don't spend the cycles
                return;
            }

            UpdateScale();   // live DPI, so an RDP connect/disconnect resizes us within a tick
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
                MarkHome();   // growth kept us where we belong — that new spot is the one to return to
                UpdateRegion();
            }
            if (!Visible) Visible = true;
            _meter.Paused = false;
            if (++_tick % 10 == 0) AssertTopmost();   // ~1s belt-and-suspenders; the foreground hook does the rest
            if (!_moved) UpdateHover(PointToClient(Cursor.Position));

            var sig = VisualSig();                    // only repaint when a tile's appearance actually changed
            if (sig != _sig) { _sig = sig; Invalidate(); }
        }
        catch { /* never let the always-on widget die on a transient error */ }
    }

    string VisualSig()
    {
        var sb = new System.Text.StringBuilder(_order.Count * 40);
        foreach (var sid in _order)
        {
            if (!_sessions.TryGetValue(sid, out var s)) continue;
            sb.Append(sid).Append(s.Working ? '1' : '0').Append(s.Hooking ? '1' : '0').Append('|');
        }
        return sb.ToString();
    }

    void ClampX()
    {
        var b = Screen.FromRectangle(Bounds).Bounds;   // the monitor it's mostly on, as EnsureOnScreen picks
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

    bool EffectiveAnchorRight() => AnchorRightFor(Bounds);

    bool AnchorRightFor(Rectangle r)
    {
        if (_anchor == "right") return true;
        if (_anchor == "left") return false;
        var wa = Screen.FromRectangle(r).WorkingArea;              // auto: which half are we on
        return r.X + r.Width / 2 >= wa.Left + wa.Width / 2;
    }

    void DockDefault()
    {
        // Centered horizontally, just above the taskbar, on the monitor with the cursor.
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - 8);
        SetHome(Location);  // an explicit placement — this is now the spot to return to
        ComputeRel();
    }

    void EnsureOnScreen()
    {
        // Full monitor bounds so it can sit on the taskbar (we keep it in front via
        // AssertTopmost); just don't let it wander off the physical screen. FromRectangle
        // picks the monitor holding most of the widget (and the nearest one if its monitor
        // just vanished), so a removed/duplicated display can't strand it.
        var b = Screen.FromRectangle(Bounds).Bounds;
        int x = Math.Clamp(Location.X, b.Left, Math.Max(b.Left, b.Right - Width));
        int y = Math.Clamp(Location.Y, b.Top, Math.Max(b.Top, b.Bottom - Height));
        if (x != Location.X || y != Location.Y) Location = new Point(x, y);
    }

    // Whether the whole widget currently sits within a single connected monitor.
    bool FullyOnScreen()
    {
        foreach (var s in Screen.AllScreens)
            if (s.Bounds.Contains(Bounds)) return true;
        return false;
    }

    // Whether the saved home spot fits some current monitor. When it doesn't, home was picked
    // on a bigger/other screen (e.g. your local 4K) and we're currently on a smaller foreign
    // one (a Remote Desktop session), so the live position is just a visibility clamp — not a
    // spot to adopt as home.
    bool HomeFitsSomeScreen() => FitsSomeScreen(new Rectangle(_home, Size));

    static bool FitsSomeScreen(Rectangle r)
    {
        foreach (var s in Screen.AllScreens)
            if (s.Bounds.Contains(r)) return true;
        return false;
    }

    // A fingerprint of the monitor layout + current DPI; changes on a resolution change, a
    // monitor added/removed/duplicated, or an RDP session swap.
    string ScreenSig()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in Screen.AllScreens)
        {
            var b = s.Bounds;
            sb.Append(b.X).Append(',').Append(b.Y).Append(',').Append(b.Width).Append('x').Append(b.Height).Append(';');
        }
        uint dpi = 0; try { if (IsHandleCreated) dpi = GetDpiForWindow(Handle); } catch { }
        return sb.Append('@').Append(dpi).ToString();
    }

    // Remember the current spot as home — but only while we're actually resting on the monitor
    // home belongs to. A temporary off-screen clamp (a monitor briefly gone, or a smaller RDP
    // screen) must never overwrite where you put it, or the widget gets stranded mid-screen when
    // you return to your real display.
    void MarkHome() { if (FullyOnScreen() && HomeFitsSomeScreen()) { SetHome(Location); ComputeRel(); } }

    void SetHome(Point p) { _home = p; _homeSet = true; _homeW = Width / _scale; }

    // _home is a top-left corner captured when the strip was _homeW logical px wide. If it is now
    // a different width (restarted with more/fewer tiles, meter toggled), keep the ANCHORED edge
    // where you put it - the same rule live growth follows - instead of the left edge, which used
    // to drift a right-anchored strip sideways on every restart. Widths are logical, so a DPI
    // change alone (an RDP session) is never mistaken for growth; and we only rebase while home's
    // own screen is present - on a foreign screen ReconcileDisplay places us by the rel spec.
    void RebaseHome()
    {
        if (!_homeSet) return;
        double now = Width / _scale;
        if (_homeW <= 0) { _homeW = now; return; }            // legacy config: adopt as-is
        if (Math.Abs(_homeW - now) < 0.5) return;
        var was = new Rectangle(_home, new Size((int)Math.Round(_homeW * _scale), Height));
        if (!FitsSomeScreen(was)) return;                     // away from home's screen: leave home alone
        if (AnchorRightFor(was)) _home.X += was.Width - Width;
        _homeW = now;
    }

    // Capture _home as an edge-relative spec: which corner it hugs, and the logical (DPI-normalized)
    // gap to that corner. This lets the exact spot be reproduced on a different-size/DPI screen
    // (e.g. a smaller RDP session) instead of being hard-clamped to the edge. Only meaningful while
    // _home sits on a real monitor.
    void ComputeRel()
    {
        if (!_homeSet) return;
        var b = Screen.FromRectangle(new Rectangle(_home, Size)).Bounds;
        _relRight  = _home.X + Width / 2 >= b.Left + b.Width / 2;
        _relBottom = _home.Y + Height / 2 >= b.Top + b.Height / 2;
        _relGapX = (_relRight  ? b.Right  - (_home.X + Width)  : _home.X - b.Left) / _scale;
        _relGapY = (_relBottom ? b.Bottom - (_home.Y + Height) : _home.Y - b.Top ) / _scale;
        _relValid = true;
    }

    // Reproduce the parked spot on the current monitor from the edge-relative spec, scaling the gap
    // to the current DPI. Used only when _home itself doesn't fit (a smaller/foreign screen), so we
    // keep the same corner + gap (e.g. left of the tray) rather than slamming against the edge.
    Point HomeFromRel()
    {
        var b = Screen.FromRectangle(new Rectangle(_home, Size)).Bounds;
        int gx = (int)Math.Round(_relGapX * _scale), gy = (int)Math.Round(_relGapY * _scale);
        int x = _relRight  ? b.Right  - Width  - gx : b.Left + gx;
        int y = _relBottom ? b.Bottom - Height - gy : b.Top  + gy;
        return new Point(x, y);
    }

    void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Fires on a background thread; hop to the UI thread before touching the form.
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(() => ReconcileDisplay("settings-changed"))); } catch { /* handle torn down mid-post */ }
    }

    // Adopt the current monitor's DPI, expressed relative to the 200% console the sizes were
    // tuned on. We query GetDpiForWindow live rather than trusting Form.DeviceDpi: DeviceDpi is
    // cached at launch and does NOT reliably refresh when Remote Desktop swaps the session DPI
    // (no WM_DPICHANGED reaches this always-on tool window), which left the widget drawing at the
    // console's 192 dpi — double size — inside a 96-dpi RDP session. Called every tick so it
    // self-heals across connect/disconnect even if no display event fires.
    void UpdateScale()
    {
        uint dpi = 0;
        try { if (IsHandleCreated) dpi = GetDpiForWindow(Handle); } catch { }
        if (dpi == 0) dpi = (uint)DeviceDpi;   // fallback if the API is unavailable
        double s = (double)dpi / RefDpi;
        _scale = s > 0 ? s : 1.0;
    }

    void ReconcileDisplay(string reason = "event")
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (_moved) { Log($"reconcile({reason}) skipped: drag in progress"); return; }   // never yank mid-drag
        try
        {
            Log($"reconcile({reason}) before :: {StateSnapshot()}");
            UpdateScale();   // a DPI change lands here (WndProc) as well as topology changes
            // A resolution / DPI / topology change (duplicate toggled on or off, a monitor of
            // a different size added or removed) can auto-resize us or leave our coordinates on
            // a monitor that momentarily changed shape. Restore our own fixed-pixel size and go
            // back to exactly where you left it — on whichever display that was. We touch the
            // position only if home no longer fits any monitor, and even then just clamp for
            // visibility WITHOUT saving, so the moment your layout is back it snaps home.
            var want = ContentSize();
            if (Size != want) Size = want;
            RebaseHome();   // home was captured at another width (e.g. tiles changed while on RDP)
            // On home's own screen, snap to the exact spot. On a smaller/foreign screen where home
            // no longer fits, reproduce the same corner + gap (left of the tray) instead of letting
            // EnsureOnScreen hard-clamp it against the edge.
            bool usedRel = _homeSet && !HomeFitsSomeScreen() && _relValid;
            if (_homeSet)
                Location = usedRel ? HomeFromRel() : _home;
            EnsureOnScreen();
            UpdateRegion();
            Invalidate();
            _screenSig = ScreenSig();   // event beat the poll to it; don't reconcile again next tick
            Log($"reconcile({reason}) after usedRel={usedRel} :: {StateSnapshot()}");
        }
        catch (Exception ex) { Log($"reconcile({reason}) EXCEPTION: {ex}"); /* never let a display event kill the widget */ }
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

    // Read allowing a concurrent writer (the shim renames .meta and we write .state) as far as
    // Windows allows: a rename over a file still fails while it's open, so writers retry, and
    // the handle is held only for the read. Returns null on any failure (missing/locked/torn)
    // so callers keep prior state.
    static string? ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch { return null; }
    }

    // Write-then-rename so a concurrent shim never reads a half-written .state (which drives
    // auto-approve). Per-process temp name so parallel writers can't clobber each other's tmp.
    // Flushed to disk first, so a power cut can't leave widget.json empty (which would cost every
    // setting). Windows refuses to rename over a file anyone has open - a hook reading this
    // .state, however it shares it - so retry briefly rather than drop the write.
    static void WriteAtomic(string path, string content)
    {
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(System.Text.Encoding.UTF8.GetBytes(content));
                fs.Flush(true);
            }
            for (int i = 0; ; i++)
            {
                try { File.Move(tmp, path, overwrite: true); break; }
                catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && i < 20) { Thread.Sleep(10); }
            }
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    void SyncSessions()
    {
        // Claude's per-session registry (files named by PID) is our liveness signal: Claude
        // drops a session's entry the moment it goes away - even on an abrupt close that skips
        // SessionEnd. It also decides which tiles exist: every live session gets one, whether or
        // not its hook has written a .meta yet. .meta supplies the auto-approval tally and the
        // status fallback.
        // Unusable = no registry folder, or entries present but none readable (format change, or every
        // file mid-write): don't prune blind. An empty-but-present folder is usable: no live sessions,
        // so the last tile goes away too.
        var reg = LoadRegistry(_pidSeen, _deadAt, out bool regUsable);
        foreach (var sid in reg.Keys) _seenInReg.Add(sid);

        // Each listed session id's tiles. A tile named by the bare id is one the hook couldn't
        // place in a process; once the registry lists that session it's either adopted (an
        // older hook's, or written in the instant before Claude listed itself) or left unshown.
        var bySid = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in reg)
        {
            if (!bySid.TryGetValue(kv.Value.Sid, out var tiles)) bySid[kv.Value.Sid] = tiles = new();
            tiles.Add(kv.Key);
        }
        AdoptBareIds(bySid);

        // /clear (and an in-place /resume) gives the same window a new session id, and the old
        // id's SessionEnd deletes its switch. Claude rewrites that window's own registry entry
        // with the new id, so spot it by entry, and the tile keeps its slot. By entry, not by
        // process: an IDE extension or the desktop app runs several sessions in one process, each
        // with its own entry, so a torn read hiding one of them can never pass the other off as
        // its successor. And the old id must really have left the registry.
        var entries = new HashSet<(string, long)>();
        foreach (var kv in reg)
        {
            if (kv.Value.Pid <= 0) continue;
            var key = (kv.Value.File, kv.Value.ProcStart);
            entries.Add(key);
            if (_fileSid.TryGetValue(key, out var old) && !old.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)
                && !reg.ContainsKey(old))
                SessionSwapped(old, kv.Key);
            _fileSid[key] = kv.Key;
        }
        // Forget an entry only after the same miss budget as a tile: one torn read of it (Claude
        // rewriting it - as /clear does) mustn't lose the swap that's about to show.
        if (regUsable)
            foreach (var key in new List<(string File, long ProcStart)>(_fileSid.Keys))
            {
                if (entries.Contains(key)) { _fileMiss.Remove(key); continue; }
                int n = _fileMiss.TryGetValue(key, out var c) ? c + 1 : 1;
                if (n >= RegMissesToPrune) { _fileSid.Remove(key); _fileMiss.Remove(key); }
                else _fileMiss[key] = n;
            }
        foreach (var kv in new List<KeyValuePair<string, (long At, bool WasOn)>>(_swapped))
            if (NowMs() - kv.Value.At > 10_000) _swapped.Remove(kv.Key);   // its new id never showed

        var metas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var f in Directory.GetFiles(SessionsDir, "*.meta")) metas[Path.GetFileNameWithoutExtension(f)] = f; }
        catch { }

        // Everything we might show or still owe cleanup: hook-written .meta files, the registry
        // (in launch order, so new tiles lay out that way), tiles on screen, and dismissals.
        var byStart = new List<KeyValuePair<string, RegInfo>>(reg);
        byStart.Sort((x, y) => x.Value.StartedAt.CompareTo(y.Value.StartedAt));
        var candidates = new List<string>(metas.Keys);
        foreach (var kv in byStart) candidates.Add(kv.Key);
        candidates.AddRange(_order);
        candidates.AddRange(_dismissed);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var live = new List<string>();
        foreach (var sid in candidates)
        {
            if (!seen.Add(sid)) continue;
            metas.TryGetValue(sid, out var metaPath);

            // A session the registry has stopped listing is gone: evict it - tile, files (an "on"
            // .state included) and any dismissal - after a few consecutive misses, so a torn read
            // of one registry file can't evict (or reorder) a live tile. A fresh .meta from a
            // session whose registry entry hasn't appeared yet is not a miss (start-up race), but
            // an OLD .meta never seen in the registry is a ghost from a session that died while
            // the widget was off. Never prune while the registry is unreadable as a whole.
            bool missing = regUsable && !reg.ContainsKey(sid)
                           && (metaPath == null || _seenInReg.Contains(sid) || IsOld(metaPath));
            if (missing)
            {
                int n = _regMiss.TryGetValue(sid, out var c) ? c + 1 : 1;
                if (n >= RegMissesToPrune)
                {
                    _regMiss.Remove(sid);
                    _dismissed.Remove(sid);
                    _lastOn.Remove(sid);
                    _tileSince.Remove(sid);
                    _userSet.Remove(sid);
                    DeleteSessionFiles(sid);
                    continue;
                }
                _regMiss[sid] = n;   // keep rendering the tile until the miss budget is spent
            }
            else _regMiss.Remove(sid);

            // A bare-id .meta for a session the registry places in a process (shared by two
            // windows, so it couldn't be adopted): not a tile of its own. Pruned once old. Unless
            // the registry itself lists it bare (an entry without a pid): then it IS the tile.
            if (!sid.Contains('@') && bySid.TryGetValue(sid, out var placed) && !placed.Contains(sid)) continue;

            if (_dismissed.Contains(sid))
            {
                if (metaPath == null) continue;   // still dismissed (Dismiss deleted its .meta)
                _dismissed.Remove(sid);           // it acted since - its tile comes back, as it always did
            }

            // `claude -r` registers a placeholder while its pick-a-session list is up: a throwaway id
            // with no status, and no hook has fired. Picking swaps the real id into that same entry.
            // Tile a session only once it's actually running - it has a status (a new session gets
            // one within a second) or its hook has written - so the list doesn't flash a tile.
            if (metaPath == null && !_sessions.ContainsKey(sid)
                && reg.TryGetValue(sid, out var pre) && pre.Status.Length == 0) continue;

            var s = _sessions.TryGetValue(sid, out var existing) ? existing : new Session();
            if (!_tileSince.ContainsKey(sid))
            {
                _tileSince[sid] = NowMs();
                if (_remembered.ContainsKey(SidOf(sid))) _rememberedAtStart.Add(sid);
            }
            string start = "";   // how this id's SessionStart came about (startup|resume|clear|compact)
            if (metaPath != null)
            {
                try
                {
                    var text = ReadShared(metaPath);
                    var m = text != null ? JsonSerializer.Deserialize<MetaDto>(text) : null;
                    if (m != null) { s.Status = m.status ?? "working"; s.Cwd = m.cwd ?? ""; s.Count = Math.Max(0, m.count); start = m.start ?? ""; }
                }
                catch { }
            }
            else if (reg.TryGetValue(sid, out var ri) && ri.Cwd.Length > 0) s.Cwd = ri.Cwd;
            var statePath = Path.Combine(SessionsDir, sid + ".state");
            var st = ReadShared(statePath);
            // A process that swapped ids carries its autopilot over - but only once this id's own
            // SessionStart says it was /clear (written since the swap, so no leftover .meta counts).
            // An in-place /resume of another conversation starts manual, like any resume.
            if (_swapped.TryGetValue(sid, out var sw))
            {
                bool fresh = start.Length > 0 && metaPath != null && MetaWrittenSince(metaPath, sw.At - 5000);
                if (fresh && start == "clear" && sw.WasOn && st == null)
                {
                    try
                    {
                        WriteAtomic(statePath, "on"); st = "on";
                        if (_remember) { _remembered[SidOf(sid)] = NowMs(); _configDirty = true; }
                    }
                    catch { }
                }
                if (fresh || NowMs() - sw.At > 10_000) _swapped.Remove(sid);
            }
            // No switch at all = this run hasn't decided: a widget start wiped it, or a resume did.
            // Only while the tile is new (its own SessionStart may still wipe the switch), it gets
            // back what you chose. A window that was already up when the widget started: exactly its
            // own last setting, so one you left manual stays manual beside one that's on. A window
            // new since (a resume): its conversation's, if that was remembered before it appeared -
            // switching one window on mustn't drag along others that just opened. And one you've
            // clicked or dismissed since gets exactly what you chose, even if its SessionStart wiped it.
            bool restore = _userSet.TryGetValue(sid, out var mine) ? mine
                         : _knownTiles.Contains(sid) ? _lastOn.Contains(sid) : _rememberedAtStart.Contains(sid);
            if (st == null && _remember && restore && NowMs() - _tileSince[sid] < RestoreWindowMs)
            {
                try { WriteAtomic(statePath, "on"); st = "on"; _remembered[SidOf(sid)] = NowMs(); _configDirty = true; } catch { }
            }
            s.Hooking = st != null && st.Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
            // Track the switch only while it exists: SessionEnd deletes it just before a swap shows.
            if (st != null && (s.Hooking ? _lastOn.Add(sid) : _lastOn.Remove(sid))) _configDirty = true;
            _sessions[sid] = s;
            live.Add(sid);
        }

        // Sweep .state files that belong to no session at all (a session that ended while we
        // weren't tracking it). Only the widget writes .state, and only for a tile, so anything
        // left over is stale - and a stale "on" is a dormant auto-approve. Throttled; cheap.
        if (regUsable && ++_stateSweep % 50 == 0)
        {
            try
            {
                foreach (var f in Directory.GetFiles(SessionsDir, "*.state"))
                    if (!seen.Contains(Path.GetFileNameWithoutExtension(f))) try { File.Delete(f); } catch { }
            }
            catch { }
        }

        if (_moved && _hitKind == Hit.Tile) return;   // don't churn order mid reorder-drag

        bool changed = false;
        for (int i = _order.Count - 1; i >= 0; i--)
            if (!live.Contains(_order[i])) { _sessions.Remove(_order[i]); _tileSince.Remove(_order[i]); _order.RemoveAt(i); changed = true; }
        foreach (var sid in live)
            if (!_order.Contains(sid))
            {
                if (_newSide == "left") _order.Insert(0, sid); else _order.Add(sid);
                changed = true;
            }
        if (changed || _configDirty) { _configDirty = false; SaveConfig(); }
        _rememberedAtStart.RemoveWhere(t => !_tileSince.ContainsKey(t));

        // Apply /name and live busy/idle status from the registry we already loaded.
        foreach (var kv in _sessions)
        {
            if (reg.TryGetValue(kv.Key, out var info))
            {
                kv.Value.Name = info.Name; kv.Value.LiveStatus = info.Status; kv.Value.StartedAt = info.StartedAt;
            }
            else { kv.Value.Name = ""; kv.Value.LiveStatus = ""; }
        }
    }

    static bool IsOld(string path)
    {
        try { return File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddSeconds(-30); }
        catch { return false; }
    }

    static bool MetaWrittenSince(string path, long unixMs)
    {
        try { return new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds() >= unixMs; }
        catch { return false; }
    }

    // The same Claude process moved from `old` to `now` (/clear, or /resume inside the session).
    // The new id takes the old tile's slot; whether autopilot follows is decided once its
    // SessionStart has said which it was (see SyncSessions).
    void SessionSwapped(string old, string now)
    {
        _swapped[now] = (NowMs(), _lastOn.Remove(old));
        int i = _order.IndexOf(old);
        if (i >= 0)
        {
            if (_order.Contains(now)) _order.RemoveAt(i); else _order[i] = now;
            _configDirty = true;
        }
        _sessions.Remove(old);
        _tileSince.Remove(old);
        _userSet.Remove(old);
        _regMiss.Remove(old);
        if (_dismissed.Remove(old)) _configDirty = true;
        DeleteSessionFiles(old);   // it has ended (its SessionEnd does the same); no lingering tile
    }

    // Read Claude Code's per-session registry (~/.claude/sessions/<pid>.json): maps our
    // session id -> what Claude knows about it. This is both our liveness signal and (since a
    // live session may have no .meta yet) the list of tiles to show. Undocumented/internal, so
    // best-effort - any failure just drops that entry and we fall back to folder + hook signal.
    static Dictionary<string, RegInfo> LoadRegistry(Dictionary<long, (string Name, long Created)> pidSeen,
                                                    Dictionary<long, long> deadAt, out bool usable)
    {
        var map = new Dictionary<string, RegInfo>(StringComparer.OrdinalIgnoreCase);
        usable = false;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");
            if (!Directory.Exists(dir)) return map;
            var files = Directory.GetFiles(dir, "*.json");
            int parsed = 0;
            var pids = new HashSet<long>();
            foreach (var f in files)
            {
                try
                {
                    var text = ReadShared(f);   // Claude may be mid-write; don't block or throw
                    if (text == null) continue;
                    using var doc = JsonDocument.Parse(text);
                    var r = doc.RootElement;
                    if (!r.TryGetProperty("sessionId", out var sidEl)) continue;
                    var sid = sidEl.GetString();
                    // The id names our files: accept only what the shim's Sanitize() leaves unchanged, so
                    // both agree on file names and nothing can point outside the sessions folder.
                    if (string.IsNullOrEmpty(sid) || !SafeId(sid)) continue;
                    parsed++;
                    // A killed or crashed Claude leaves its entry behind (only a clean exit removes
                    // it), so check the process is really still there.
                    long pid = r.TryGetProperty("pid", out var pe) && pe.TryGetInt64(out var pv) ? pv : 0;
                    long procStart = r.TryGetProperty("procStart", out var ps) && ps.ValueKind == JsonValueKind.String
                                     && long.TryParse(ps.GetString(), out var pst) ? pst : 0;
                    if (pid > 0)
                    {
                        pids.Add(pid);
                        // Once seen dead, an entry stays dead for as long as it's left unchanged -
                        // whatever process Windows hands its pid to next (a hook's shell, git...),
                        // which would otherwise bring the dead tile back. Rewritten means a new
                        // Claude got that pid: judge it afresh.
                        long written;
                        try { written = File.GetLastWriteTimeUtc(f).Ticks; } catch { written = 0; }
                        if (deadAt.TryGetValue(pid, out var was))
                        {
                            if (written == 0 || written == was) continue;
                            deadAt.Remove(pid); pidSeen.Remove(pid);
                        }
                        if (!ProcessAlive(pid, pidSeen)) { deadAt[pid] = written; continue; }
                    }
                    var name = r.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                    var status = r.TryGetProperty("status", out var s) ? (s.GetString() ?? "") : "";
                    var cwd = r.TryGetProperty("cwd", out var c) ? (c.GetString() ?? "") : "";
                    long started = r.TryGetProperty("startedAt", out var t)
                                   && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var ms) ? ms : 0;
                    // Keyed by tile: the same conversation open in two windows is two sessions.
                    map[pid > 0 ? sid + "@" + pid : sid] = new RegInfo(sid, name, status, cwd, started, pid, procStart, Path.GetFileName(f));
                }
                catch { }
            }
            usable = files.Length == 0 || parsed > 0;   // all-dead is still a readable registry
            if (usable)
            {
                foreach (var pid in new List<long>(pidSeen.Keys))
                    if (!pids.Contains(pid)) pidSeen.Remove(pid);
                foreach (var pid in new List<long>(deadAt.Keys))
                    if (!pids.Contains(pid)) deadAt.Remove(pid);
            }
        }
        catch { }
        return map;
    }

    // Is this pid still the process Claude registered? Dead = no such process, it has exited, or the
    // pid now belongs to a different process (a crashed Claude's pid reused): its exe name or creation
    // time differs from what WE saw the first time this pid was listed. Both come from Windows, so
    // this holds however Claude is installed. Deliberately NOT compared against the entry's procStart:
    // that doesn't match the creation time exactly on every machine, and a mismatch evicted every
    // live, idle session (v1.0.26-29). Anything we can't determine counts as alive.
    static bool ProcessAlive(long pid, Dictionary<long, (string Name, long Created)> pidSeen)
    {
        if (pid > uint.MaxValue) return true;
        // Seen dead: forget what this pid was, so a Claude that Windows later gives the same pid
        // (rewriting the dead one's entry, which a kill leaves behind) is judged afresh.
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero)
        {
            if (Marshal.GetLastWin32Error() != ERROR_INVALID_PARAMETER) return true;   // 87 = no such process
            pidSeen.Remove(pid);
            return false;
        }
        try
        {
            if (GetExitCodeProcess(h, out uint code) && code != STILL_ACTIVE) { pidSeen.Remove(pid); return false; }
            var sb = new System.Text.StringBuilder(1024);
            int len = sb.Capacity;
            if (!QueryFullProcessImageName(h, 0, sb, ref len)
                || !GetProcessTimes(h, out long created, out _, out _, out _)) return true;
            var now = (Name: Path.GetFileName(sb.ToString()), Created: created);
            if (!pidSeen.TryGetValue(pid, out var first)) { pidSeen[pid] = now; return true; }
            return first.Created == now.Created && string.Equals(first.Name, now.Name, StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseHandle(h); }
    }
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, STILL_ACTIVE = 259;
    const int ERROR_INVALID_PARAMETER = 87;
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, uint flags, System.Text.StringBuilder name, ref int size);

    static bool SafeId(string sid)
    {
        foreach (var ch in sid)
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_')) return false;
        return true;
    }

    // A tile id: a safe session id, optionally "@<pid>". Names files, so nothing else gets in.
    static bool SafeTileId(string id)
    {
        int at = id.IndexOf('@');
        if (at < 0) return id.Length > 0 && SafeId(id);
        if (at == 0 || at == id.Length - 1 || !SafeId(id[..at])) return false;
        foreach (var ch in id[(at + 1)..]) if (!char.IsAsciiDigit(ch)) return false;
        return true;
    }

    static string SidOf(string tile) { int at = tile.IndexOf('@'); return at < 0 ? tile : tile[..at]; }

    const long RestoreWindowMs = 15_000;   // how long a new tile may still get its remembered autopilot

    // Bare session ids (from before tiles were per process, or written by the hook in the instant
    // before Claude listed itself) become the session's tile when it's in exactly one process:
    // its place in the strip, a dismissal, and its .meta (tally) carry over. A session in two
    // processes can't be told apart, so its bare files are left to age out.
    void AdoptBareIds(Dictionary<string, List<string>> bySid)
    {
        for (int i = 0; i < _order.Count; i++)
        {
            var id = _order[i];
            if (id.Contains('@') || !bySid.TryGetValue(id, out var tiles) || tiles.Count != 1 || _order.Contains(tiles[0])) continue;
            _order[i] = tiles[0];
            if (_sessions.Remove(id, out var s)) _sessions[tiles[0]] = s;
            if (_tileSince.Remove(id, out var since)) _tileSince[tiles[0]] = since;
            _configDirty = true;
        }
        foreach (var id in new List<string>(_dismissed))
            if (!id.Contains('@') && bySid.TryGetValue(id, out var tiles))
            {
                _dismissed.Remove(id);
                foreach (var t in tiles) _dismissed.Add(t);
                _configDirty = true;
            }
        try
        {
            foreach (var f in Directory.GetFiles(SessionsDir, "*.meta"))
            {
                var id = Path.GetFileNameWithoutExtension(f);
                if (id.Contains('@') || !bySid.TryGetValue(id, out var tiles) || tiles.Count != 1) continue;
                var to = Path.Combine(SessionsDir, tiles[0] + ".meta");
                if (!File.Exists(to)) try { File.Move(f, to); } catch { }
            }
        }
        catch { }
    }

    // Stop remembering a conversation's autopilot - unless another of its windows is still on it.
    void ForgetUnlessOn(string tile)
    {
        var sid = SidOf(tile);
        foreach (var kv in _sessions)
            if (kv.Value.Hooking && !kv.Key.Equals(tile, StringComparison.OrdinalIgnoreCase)
                && SidOf(kv.Key).Equals(sid, StringComparison.OrdinalIgnoreCase)) return;
        _remembered.Remove(sid);
    }

    // Turn every session's autopilot off. Per file, so one locked file can't spare the rest.
    internal static void WipeStates()
    {
        string[] files;
        try { files = Directory.GetFiles(SessionsDir, "*.state"); } catch { return; }
        foreach (var f in files) try { File.Delete(f); } catch { }
    }

    // What we keep from one registry entry.
    readonly record struct RegInfo(string Sid, string Name, string Status, string Cwd, long StartedAt, long Pid, long ProcStart, string File);

    // Lowercase keys mirror the .meta JSON the shim writes.
    sealed class MetaDto
    {
        public string status { get; set; } = "working";
        public string start { get; set; } = "";
        public string cwd { get; set; } = "";
        public long count { get; set; }
    }

    // ---- geometry --------------------------------------------------------

    Rectangle GripRect() => new(Pad, Pad, Grip, Tile);
    // The meter is exactly two tiles wide (gap included) and always first: right of the grip,
    // left of every session tile. Session tiles start after it, so it is never a drop target.
    const int MeterSlots = 2;
    int MeterW => Tile * MeterSlots + Gap * (MeterSlots - 1);
    int TilesX => Pad + Grip + Gap + (_meterOn ? MeterW + Gap : 0);
    Rectangle MeterRect() => new(Pad + Grip + Gap, Pad, MeterW, Tile);
    Rectangle TileRect(int i) => new(TilesX + i * (Tile + Gap), Pad, Tile, Tile);

    Hit HitTest(Point p, out int index)
    {
        index = -1;
        if (GripRect().Contains(p)) return Hit.Grip;
        if (_meterOn && MeterRect().Contains(p)) return Hit.Meter;
        for (int i = 0; i < _order.Count; i++)
            if (TileRect(i).Contains(p)) { index = i; return Hit.Tile; }
        return Hit.None;
    }

    int SlotFromX(int x) =>
        _order.Count == 0 ? -1   // Math.Clamp(_, 0, -1) would throw; no slots to target anyway
            : Math.Clamp((int)Math.Round((x - TilesX) / (double)(Tile + Gap)), 0, _order.Count - 1);

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
                    g.FillEllipse(dot, gr.X + Sc(3) + cx * Sc(6), gr.Y + Tile / 2 - Sc(11) + cy * Sc(8), Sc(4), Sc(4));

        if (_meterOn) DrawMeter(g, MeterRect());

        bool lifting = _moved && _hitKind == Hit.Tile && _dragSid != null;
        int liftIdx = lifting ? _order.IndexOf(_dragSid!) : -1;

        for (int i = 0; i < _order.Count; i++)
        {
            if (i == liftIdx) { DrawEmptySlot(g, TileRect(i)); continue; }  // the hole the held tile left behind
            // TryGetValue, not indexer: OnPaint runs OUTSIDE Tick's try/catch, so a transient
            // _order/_sessions desync must skip a tile, never throw and wedge painting.
            if (_sessions.TryGetValue(_order[i], out var s)) g.DrawImage(TileFor(s), TileRect(i));
        }

        if (lifting && _sessions.TryGetValue(_dragSid!, out var ds)) DrawLiftedTile(g, TileFor(ds));
    }

    // A recessed slot showing where the held tile will drop.
    void DrawEmptySlot(Graphics g, Rectangle r)
    {
        using var p = Rounded(Rectangle.Inflate(r, -Sc(2), -Sc(2)), Radius - Sc(3));
        using var fill = new SolidBrush(Color.FromArgb(90, 0, 0, 0));
        g.FillPath(fill, p);
        using var edge = new Pen(Color.FromArgb(30, 255, 255, 255));
        g.DrawPath(edge, p);
    }

    // The picked-up tile: floats a few px above the strip, tracks the pointer, scaled up a
    // touch with a soft ground shadow so it reads as lifted off the surface.
    void DrawLiftedTile(Graphics g, Bitmap img)
    {
        if (_order.Count == 0) return;   // TileRect(-1) would be garbage; nothing to carry
        int Lift = Sc(5), Grow = Sc(2);
        int minX = TileRect(0).X, maxX = TileRect(_order.Count - 1).X;
        int left = Math.Clamp(_dragPos.X - _dragGrabDX, minX, maxX);

        // Ground shadow, directly under where the tile is being carried.
        using (var shadow = Rounded(new Rectangle(left + Sc(1), Pad + Sc(4), Tile, Tile - Sc(2)), Radius))
        using (var sb = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            g.FillPath(sb, shadow);

        g.DrawImage(img, new Rectangle(left - Grow, Pad - Lift - Grow, Tile + Grow * 2, Tile + Grow * 2));
    }

    // Bar colour by load, in the mascot tiles' own tints (assets/make_icons.py TINTS) plus a
    // matching red: green under 25%, yellow 25-50%, red over 50%.
    static readonly Color MeterLow = Color.FromArgb(43, 166, 82);     // = waiting-tile green
    static readonly Color MeterMid = Color.FromArgb(232, 176, 28);    // = working-tile yellow
    static readonly Color MeterHigh = Color.FromArgb(214, 64, 52);
    static Color MeterColor(double v) => v < 25 ? MeterLow : v <= 50 ? MeterMid : MeterHigh;

    // The system meter: four vertical bars filling from the bottom - CPU, Memory, Network, GPU,
    // left to right - on a tile-shaped plate. No labels; the hover tooltip says which is which.
    // Values come from the sampler's latest snapshot (empty tracks until the first one lands).
    void DrawMeter(Graphics g, Rectangle r)
    {
        using (var plate = Rounded(r, Math.Max(1, Sc(4))))   // near-square, like the tiles
        using (var fill = new SolidBrush(Color.FromArgb(46, 46, 53)))
            g.FillPath(fill, plate);

        var snap = _meter.Latest;
        double[] vals = snap == null ? new double[4] : new[] { snap.Cpu, snap.Mem, snap.Net, snap.Gpu };

        int pad = Sc(6), gapX = Sc(5);
        int colW = Math.Max(1, (r.Width - pad * 2 - gapX * 3) / 4);
        int left = r.X + (r.Width - (colW * 4 + gapX * 3)) / 2;   // centre the leftover pixel or two
        int top = r.Y + pad, colH = Math.Max(1, r.Height - pad * 2);

        using var trackBrush = new SolidBrush(Color.FromArgb(24, 24, 28));
        for (int i = 0; i < vals.Length; i++)
        {
            var track = new Rectangle(left + i * (colW + gapX), top, colW, colH);
            using var path = Rounded(track, Math.Max(1, Math.Min(Sc(3), colW / 2)));
            g.FillPath(trackBrush, path);

            double v = Math.Clamp(vals[i], 0, 100);
            int h = (int)Math.Round(colH * v / 100);
            if (v > 0) h = Math.Max(h, Sc(2));   // any activity at all stays visible (network idles near 0%)
            if (h <= 0) continue;
            var state = g.Save();
            g.SetClip(path, CombineMode.Intersect);   // bar keeps the track's rounded ends at any height
            using (var bar = new SolidBrush(MeterColor(v)))
                g.FillRectangle(bar, track.X, track.Bottom - h, track.Width, h);
            g.Restore(state);
        }
    }

    Bitmap TileFor(Session s) =>
        s.Working ? (s.Hooking ? _workOn : _workOff)
                  : (s.Hooking ? _waitOn : _waitOff);

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
            _dragGrabDX = _hitKind == Hit.Tile ? e.X - TileRect(idx).X : 0;
            _dragPos = e.Location;
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
            if (!_moved && (Math.Abs(now.X - _downScreen.X) > Sc(DragThreshold) || Math.Abs(now.Y - _downScreen.Y) > Sc(DragThreshold)))
                _moved = true;

            if (_moved)
            {
                if (_hitKind == Hit.Grip && !_locked)
                    Location = new Point(_downFormLoc.X + (now.X - _downScreen.X), _downFormLoc.Y + (now.Y - _downScreen.Y));
                else if (_hitKind == Hit.Tile && _dragSid != null)
                {
                    _dragPos = e.Location;
                    int target = SlotFromX(e.X), cur = _order.IndexOf(_dragSid);
                    if (cur >= 0 && target >= 0 && target != cur)   // cur < 0: its session vanished mid-drag
                    {
                        _order.RemoveAt(cur);
                        _order.Insert(target, _dragSid);
                    }
                    Invalidate();   // repaint every move so the lifted tile tracks the pointer
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
                // You moved it - new home. Not when locked: the grip doesn't move a locked strip, and
                // adopting Location then could save a temporary clamp (e.g. on an RDP screen) as home.
                if (_hitKind == Hit.Grip && !_locked) { EnsureOnScreen(); SetHome(Location); ComputeRel(); }
                SaveConfig();
            }
        }
        _hitKind = Hit.None;
        _dragSid = null;
        _moved = false;
        Invalidate();   // settle the lifted tile back into its slot the instant you release
        base.OnMouseUp(e);
    }

    // Focus yanked away mid-drag (Alt-Tab, the Win key, a UAC prompt) and the button-up never
    // arrives. Finish the drag right here, or tile sync and display handling stay paused until
    // your next click. (A plain click, never moved, is left for OnMouseUp to toggle.)
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture && _moved)
        {
            if (_hitKind == Hit.Grip && !_locked) { EnsureOnScreen(); SetHome(Location); ComputeRel(); }
            SaveConfig();
            _hitKind = Hit.None;
            _dragSid = null;
            _moved = false;
            Invalidate();
        }
        base.OnMouseCaptureChanged(e);
    }

    const string MeterHoverKey = "meter:";   // hover key for the meter; can't collide with a session id

    void UpdateHover(Point p)
    {
        var kind = HitTest(p, out int idx);
        string key = "", text = "";
        var r = Rectangle.Empty;
        if (kind == Hit.Tile && _sessions.ContainsKey(_order[idx])) { key = _order[idx]; text = TileTooltip(key); r = TileRect(idx); }
        else if (kind == Hit.Meter) { key = MeterHoverKey; text = MeterTooltip(); r = MeterRect(); }
        if (key == _hoverSid && text == _hoverText) return;   // refresh when target OR its data changes
        _hoverSid = key;
        _hoverText = text;
        if (text.Length == 0) { _tip.HideTip(); return; }
        int anchorX = PointToScreen(new Point(r.X + r.Width / 2, r.Y)).X;   // center of the hovered tile
        _tip.ShowTip(text, Bounds, anchorX);
    }

    // Tick re-runs UpdateHover every 100 ms, so this live text refreshes as each sample lands.
    string MeterTooltip() => _meter.Latest?.Describe() ?? "System meter\nsampling...";

    string TileTooltip(string sid)
    {
        var s = _sessions[sid];
        var enabled = s.Hooking ? "hooking" : "manual";
        var need = s.Working ? "working" : "waiting on you";
        return $"{Label(sid)}\n{enabled} · {need}\n{s.Count} auto-approval{(s.Count == 1 ? "" : "s")}";
    }

    static string DisplayName(Session s)
    {
        if (!string.IsNullOrWhiteSpace(s.Name)) return s.Name;
        if (s.Cwd.Length == 0) return "session";
        var leaf = Path.GetFileName(s.Cwd.TrimEnd('/', '\\'));
        return leaf.Length > 0 ? leaf : s.Cwd;   // a drive root (C:\) has no folder name
    }

    void ToggleSession(string sid)
    {
        if (!_sessions.TryGetValue(sid, out var s)) return;
        s.Hooking = !s.Hooking;
        try
        {
            Directory.CreateDirectory(SessionsDir);
            WriteAtomic(Path.Combine(SessionsDir, sid + ".state"), s.Hooking ? "on" : "off");
        }
        catch
        {
            s.Hooking = !s.Hooking;   // didn't take: show what's really in effect, and say so
            System.Media.SystemSounds.Hand.Play();
            Invalidate();
            return;
        }
        _userSet[sid] = s.Hooking;   // your call from here on: Remember never overrides it
        if (_remember)
        {
            if (s.Hooking) _remembered[SidOf(sid)] = NowMs(); else ForgetUnlessOn(sid);
            SaveConfig();
        }
        Invalidate();
    }

    // Turning it on remembers whatever is on autopilot right now; turning it off forgets everything,
    // so switching it back on later can't resurrect old choices.
    void ToggleRemember()
    {
        _remember = !_remember;
        _remembered.Clear();
        if (_remember)
            foreach (var kv in _sessions)
                if (kv.Value.Hooking) _remembered[SidOf(kv.Key)] = NowMs();
        RefreshMenuChecks();
        SaveConfig();
    }

    static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    void ToggleLock() { _locked = !_locked; RefreshMenuChecks(); SaveConfig(); Invalidate(); }
    void SetNewSide(string v) { _newSide = v; RefreshMenuChecks(); SaveConfig(); }
    void SetAnchor(string v) { _anchor = v; RefreshMenuChecks(); SaveConfig(); }

    void ToggleMeter()
    {
        _meterOn = !_meterOn;
        if (_meterOn) _meter.Start(); else _meter.Stop();   // off = no sampling at all
        RefreshMenuChecks();
        SaveConfig();
        Tick();         // resize by two tiles, keeping the anchored edge (and show/hide if it's all there is)
        Invalidate();   // every session tile shifted
    }

    // ---- fullscreen guard ------------------------------------------------

    bool ShouldHideForFullscreen()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle) return false;
            if (fg == GetShellWindow() || IsDesktopClass(fg)) return false;   // clicked the desktop / Win+D

            var myMon = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
            var fgMon = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
            if (myMon != fgMon) return false;

            if (!GetWindowRect(fg, out RECT wr)) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(fgMon, ref mi)) return false;

            bool coversMonitor = wr.left <= mi.rcMonitor.left && wr.top <= mi.rcMonitor.top &&
                                 wr.right >= mi.rcMonitor.right && wr.bottom >= mi.rcMonitor.bottom;
            if (!coversMonitor) return false;

            // A maximized ordinary window also covers the whole monitor when the taskbar is
            // auto-hidden. Only treat it as fullscreen if it has no title bar (a game or
            // borderless app), so the widget doesn't vanish while you work maximized.
            return (GetWindowLong(fg, GWL_STYLE) & WS_CAPTION) == 0;
        }
        catch { return false; }
    }

    // ---- diagnostics -----------------------------------------------------

    // Append a timestamped line to the in-memory ring buffer (bounded, never touches disk).
    void Log(string msg)
    {
        try
        {
            _log.Enqueue($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}");
            while (_log.Count > LogCap) _log.Dequeue();
        }
        catch { }
    }

    uint SafeDpi() { try { return IsHandleCreated ? GetDpiForWindow(Handle) : 0; } catch { return 0; } }

    // One-line snapshot of everything relevant to sizing/positioning across a display change.
    string StateSnapshot()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"dpi={SafeDpi()} scale={_scale:0.###} loc={Location.X},{Location.Y} size={Size.Width}x{Size.Height} ");
        sb.Append($"home={_home.X},{_home.Y} homeFits={HomeFitsSomeScreen()} ");
        sb.Append($"rel[valid={_relValid} right={_relRight} bottom={_relBottom} gapX={_relGapX:0.#} gapY={_relGapY:0.#}] ");
        sb.Append($"locked={_locked} anchor={_anchor} newSide={_newSide} tiles={_order.Count} screens=");
        foreach (var s in Screen.AllScreens)
        {
            var b = s.Bounds;
            sb.Append($"[{b.X},{b.Y} {b.Width}x{b.Height}{(s.Primary ? "*" : "")}]");
        }
        return sb.ToString();
    }

    // Right-click -> Save debug log: flush the ring buffer to a timestamped file and reveal it.
    void SaveDebugLog()
    {
        try
        {
            Directory.CreateDirectory(HookerDir);
            var path = Path.Combine(HookerDir, $"widget-debug-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var header = $"Hooker {_version} debug log  ({DateTime.Now:yyyy-MM-dd HH:mm:ss})\r\nNOW :: {StateSnapshot()}\r\n----\r\n";
            File.WriteAllText(path, header + string.Join("\r\n", _log.ToArray()) + "\r\n");
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
        }
        catch { }
    }

    // ---- persistence -----------------------------------------------------

    void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var c = ParseConfig(File.ReadAllText(ConfigPath));
                if (c != null)
                {
                    // Coerce hand-edited / corrupt values to valid ones, and drop null/blank or
                    // duplicate sids, so a bad widget.json can't wedge the menu or double a tile.
                    _locked = c.Locked;
                    _anchor = c.Anchor is "left" or "right" or "auto" ? c.Anchor : "auto";
                    _newSide = c.NewSide is "left" or "right" ? c.NewSide : "right";
                    _meterOn = c.Meter;
                    // Ids name files in the sessions folder: only ones that can't point anywhere else.
                    foreach (var sid in c.Order ?? new List<string>())
                        if (!string.IsNullOrEmpty(sid) && SafeTileId(sid) && !_order.Contains(sid)) _order.Add(sid);
                    foreach (var sid in c.Dismissed ?? new List<string>())
                        if (!string.IsNullOrEmpty(sid) && SafeTileId(sid)) _dismissed.Add(sid);
                    _remember = c.RememberAutopilot;
                    if (_remember)
                    {
                        long now = NowMs(), cutoff = now - RememberDays * 86_400_000;
                        // A stamp from the future (clock was wrong) would never age out: drop it.
                        foreach (var kv in c.Autopilot ?? new Dictionary<string, long>())
                            if (kv.Key.Length > 0 && SafeId(kv.Key) && kv.Value >= cutoff && kv.Value <= now + 86_400_000)
                                _remembered[kv.Key] = kv.Value;
                        // Which windows were on. Only with Remember: what it says is "on" also counts
                        // as on for a /clear carry-over, and without Remember every window starts manual.
                        foreach (var sid in c.OnTiles ?? new List<string>())
                            if (!string.IsNullOrEmpty(sid) && SafeTileId(sid)) _lastOn.Add(sid);
                    }
                    // Windows up before this start: Remember gives each back its own setting.
                    _knownTiles.UnionWith(_order); _knownTiles.UnionWith(_dismissed); _knownTiles.UnionWith(_lastOn);
                    // HomeSet, or (legacy configs, which used -1 for "unset") non-negative coords.
                    if (c.HomeSet || (c.X >= 0 && c.Y >= 0))
                    {
                        _home = new Point(c.X, c.Y); _homeSet = true;
                        _homeW = double.IsFinite(c.HomeW) ? Math.Max(0, c.HomeW) : 0;
                        Location = _home;
                    }
                    _relRight = c.HomeRight; _relBottom = c.HomeBottom;
                    _relGapX = c.HomeGapX; _relGapY = c.HomeGapY;
                    _relValid = c.HomeGapX >= 0 && c.HomeGapY >= 0;
                }
            }
        }
        catch { }
        RefreshMenuChecks();
    }

    // One bad value (a hand edit, a null) mustn't cost every other setting: if the whole file
    // won't bind, take each setting that does.
    static WidgetConfig? ParseConfig(string text)
    {
        try { return JsonSerializer.Deserialize<WidgetConfig>(text); }
        catch (JsonException) { }
        using var doc = JsonDocument.Parse(text);   // not JSON at all: throws, and defaults stand
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        var c = new WidgetConfig();
        foreach (var prop in typeof(WidgetConfig).GetProperties())
            if (prop.CanWrite && doc.RootElement.TryGetProperty(prop.Name, out var v))
                try { var val = v.Deserialize(prop.PropertyType); if (val != null) prop.SetValue(c, val); } catch { }
        return c;
    }

    void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(HookerDir);
            WriteAtomic(ConfigPath, JsonSerializer.Serialize(new WidgetConfig
            {
                // Persist _home, never the live Location — the live one may be a temporary
                // clamp to stay visible while a monitor is missing, which must not become the
                // remembered spot.
                X = _home.X, Y = _home.Y, HomeSet = _homeSet, HomeW = _homeW, Locked = _locked, Meter = _meterOn,
                Anchor = _anchor, NewSide = _newSide, Order = new(_order), Dismissed = new(_dismissed),
                RememberAutopilot = _remember, Autopilot = new(_remembered), OnTiles = new(_lastOn),
                HomeRight = _relRight, HomeBottom = _relBottom,
                HomeGapX = _relValid ? _relGapX : -1, HomeGapY = _relValid ? _relGapY : -1,
            }));
        }
        catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_winEventHook != IntPtr.Zero) { UnhookWinEvent(_winEventHook); _winEventHook = IntPtr.Zero; }
        foreach (var sid in _order)                 // never leave a session auto-approving
            try { WriteAtomic(Path.Combine(SessionsDir, sid + ".state"), "off"); } catch { }
        SaveConfig();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            if (_winEventHook != IntPtr.Zero) { UnhookWinEvent(_winEventHook); _winEventHook = IntPtr.Zero; }
            _poll?.Dispose();
            _meter.Dispose();
            _menu?.Dispose();
            _tip?.Dispose();
            _workOn?.Dispose(); _workOff?.Dispose(); _waitOn?.Dispose(); _waitOff?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- Win32 -----------------------------------------------------------

    const uint MONITOR_DEFAULTTONEAREST = 2;
    const int GWL_STYLE = -16;
    const int WS_CAPTION = 0x00C00000;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0x0000;

    void AssertTopmost()
    {
        try { SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER); }
        catch { }
    }

    void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time)
    {
        // An OUTOFCONTEXT event already queued before UnhookWinEvent can still dispatch during
        // teardown; guard + swallow so touching a disposing form never throws (this callback,
        // unlike Tick, has no outer safety net).
        if (IsDisposed || !IsHandleCreated) return;
        try { if (Visible && !ShouldHideForFullscreen()) AssertTopmost(); } catch { }
    }

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int nIndex);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

    // The desktop is a borderless window covering the screen, so it would pass for a fullscreen app.
    static bool IsDesktopClass(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(32);
        if (GetClassName(h, sb, sb.Capacity) == 0) return false;
        var c = sb.ToString();
        return c == "Progman" || c == "WorkerW";
    }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventProc proc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hWinEventHook);

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
        int x = Math.Clamp(anchorCenterX - Width / 2, scr.Bounds.Left, Math.Max(scr.Bounds.Left, scr.Bounds.Right - Width));
        y = Math.Clamp(y, scr.Bounds.Top, Math.Max(scr.Bounds.Top, scr.Bounds.Bottom - Height));
        Location = new Point(x, y);

        if (!Visible) Show();
    }

    public void HideTip() { if (Visible) Hide(); }
}
