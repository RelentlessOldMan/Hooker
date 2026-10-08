// System meter: the four headline numbers from Task Manager's Performance tab - CPU, Memory,
// Network, GPU - for the widget's left-most "two-tile" bar meter, plus the detail its hover
// tooltip shows. Sampled once a second on a thread-pool timer (never the UI thread) and
// published as an immutable snapshot the widget just reads.
//
// In-box Windows APIs only - PDH performance counters (the same ones Task Manager reads),
// DXGI for GPU names/VRAM, D3DKMT for GPU temperature/fan (Task Manager's source too), and
// psapi/kernel32 for memory and process counts - so no NuGet and no WMI. Every probe is
// best-effort: one that fails just leaves its field empty, and nothing here ever throws
// into the widget.

using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HookerWidget;

sealed class GpuStat
{
    public string Name = "";
    public double Util;                    // % - busiest engine, the way Task Manager reports it
    public ulong DedicatedUsed, DedicatedTotal, SharedUsed;
    public double TempC;                   // 0 = the driver doesn't report it
    public uint FanRpm;
}

sealed class MeterSnapshot
{
    // Bar values, 0..100.
    public double Cpu, Mem, Net, Gpu;

    public string CpuName = "";
    public double CpuGhz, CpuBaseGhz;
    public int Cores, Logical;
    public uint Processes, Threads, Handles;
    public TimeSpan Uptime;

    public ulong MemTotal, MemAvail, MemCached, CommitUsed, CommitLimit;

    public string NetName = "";
    public double NetSendBps, NetRecvBps;  // bits/s
    public long NetLinkBps;

    public List<GpuStat> Gpus = new();

    // The hover tooltip: one block per meter row, in the bars' top-to-bottom order.
    public string Describe()
    {
        var sb = new StringBuilder();

        sb.Append($"CPU  {Cpu:0}%");
        if (CpuGhz > 0) sb.Append($"  ·  {CpuGhz:0.00} GHz");
        sb.AppendLine();
        if (CpuName.Length > 0) sb.AppendLine(CpuName);
        var cpu = new List<string>();
        if (Cores > 0) cpu.Add($"{Cores} cores");
        if (Logical > 0) cpu.Add($"{Logical} logical processors");
        if (CpuBaseGhz > 0) cpu.Add($"base {CpuBaseGhz:0.00} GHz");
        if (cpu.Count > 0) sb.AppendLine(string.Join(" · ", cpu));
        if (Processes > 0) sb.AppendLine($"{Processes:N0} processes · {Threads:N0} threads · {Handles:N0} handles");
        sb.AppendLine($"Up time {(int)Uptime.TotalDays}:{Uptime.Hours:00}:{Uptime.Minutes:00}:{Uptime.Seconds:00}");

        sb.AppendLine();
        sb.AppendLine($"Memory  {Mem:0}%  ·  {Gb(MemTotal - MemAvail)} / {Gb(MemTotal)} GB in use");
        sb.AppendLine($"Available {Gb(MemAvail)} GB · Cached {Gb(MemCached)} GB");
        if (CommitLimit > 0) sb.AppendLine($"Committed {Gb(CommitUsed)} / {Gb(CommitLimit)} GB");

        sb.AppendLine();
        sb.Append($"Network  {Net:0}%");
        if (NetName.Length > 0) sb.Append($"  ·  {NetName}");
        sb.AppendLine();
        sb.Append($"Send {Bits(NetSendBps)} · Receive {Bits(NetRecvBps)}");
        if (NetLinkBps > 0) sb.Append($" · link {Bits(NetLinkBps)}");
        sb.AppendLine();

        if (Gpus.Count == 0) { sb.AppendLine(); sb.AppendLine("GPU  n/a"); }
        for (int i = 0; i < Gpus.Count; i++)
        {
            var g = Gpus[i];
            sb.AppendLine();
            sb.AppendLine($"GPU{(Gpus.Count > 1 ? " " + i : "")}  {g.Util:0}%  ·  {g.Name}");
            var d = new List<string>();
            if (g.DedicatedTotal > 0) d.Add($"Dedicated {Gb(g.DedicatedUsed)} / {Gb(g.DedicatedTotal)} GB");
            if (g.DedicatedTotal < (1UL << 30) && g.SharedUsed > 0) d.Add($"Shared {Gb(g.SharedUsed)} GB");   // integrated GPUs live in shared memory
            if (g.TempC > 0) d.Add($"{g.TempC:0} °C");
            if (g.FanRpm > 0) d.Add($"fan {g.FanRpm:N0} RPM");
            if (d.Count > 0) sb.AppendLine(string.Join(" · ", d));
        }
        return sb.ToString().TrimEnd();
    }

    static string Gb(ulong bytes) => (bytes / 1073741824.0).ToString("0.0");

    static string Bits(double bps) =>
        bps >= 1e9 ? $"{bps / 1e9:0.#} Gbps" :
        bps >= 1e6 ? $"{bps / 1e6:0.#} Mbps" :
        bps >= 1e3 ? $"{bps / 1e3:0.#} Kbps" : $"{bps:0} bps";
}

sealed class SystemMeter : IDisposable
{
    const int PeriodMs = 1000;

    // Two locks so the UI thread never waits out a sample: _timerLock guards the timer and is
    // only ever held briefly (Start/Stop from the menu, re-arming); _sampleLock serialises the
    // sampling itself and the PDH query's lifetime.
    readonly object _timerLock = new();
    readonly object _sampleLock = new();
    System.Threading.Timer? _timer;
    int _gen;   // bumped by every Start; each callback carries the generation of the timer that fired it
    volatile bool _disposed;
    volatile bool _needPrime;   // take a baseline instead of publishing (first sample, or after a pause)
    bool _inited;
    MeterSnapshot? _latest;

    // Newest sample, or null before the first one (or while stopped).
    public MeterSnapshot? Latest => Volatile.Read(ref _latest);
    // Set while nobody can see the meter (widget hidden for a fullscreen game): skip the work.
    public volatile bool Paused;
    // Raised on a thread-pool thread after each sample.
    public event Action? Sampled;

    public void Start()
    {
        lock (_timerLock)
        {
            if (_disposed || _timer != null) return;
            _needPrime = true;
            _timer = new System.Threading.Timer(Run, ++_gen, 0, Timeout.Infinite);
        }
    }

    public void Stop()
    {
        lock (_timerLock)
        {
            _timer?.Dispose();
            _timer = null;
            Volatile.Write(ref _latest, null);
        }
    }

    public void Dispose()
    {
        lock (_timerLock)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
        // At shutdown it's fine to wait for an in-flight sample before closing the query.
        lock (_sampleLock)
        {
            if (_query != IntPtr.Zero) { try { PdhCloseQuery(_query); } catch { } _query = IntPtr.Zero; }
        }
    }

    // One-shot timer re-armed after each sample, so a slow sample can never overlap the next.
    void Run(object? state)
    {
        // A callback queued by a timer that has since been stopped (and maybe replaced by a quick
        // off/on) is stale: it must not eat the new timer's priming sample or re-arm it.
        int gen = (int)state!;
        bool Current() => _timer != null && gen == _gen;   // call under _timerLock
        lock (_timerLock) { if (!Current()) return; }

        MeterSnapshot? snap = null;
        lock (_sampleLock)
        {
            if (_disposed) return;
            lock (_timerLock) { if (!Current()) return; }
            if (Paused) _needPrime = true;   // on resume: fresh baseline, not one rate averaged over the pause
            else try { snap = Sample(); } catch { }
        }

        lock (_timerLock)
        {
            if (!Current()) return;          // stopped mid-sample: don't publish into a stopped meter
            if (snap != null) Volatile.Write(ref _latest, snap);
            _timer!.Change(PeriodMs, Timeout.Infinite);
        }
        if (snap != null) try { Sampled?.Invoke(); } catch { }
    }

    // ---- sampling ---------------------------------------------------------

    string _cpuName = "";
    double _cpuBaseMhzReg;
    int _cores;

    sealed record Adapter(ulong Luid, uint LuidLow, int LuidHigh, string Name, ulong Dedicated);
    readonly List<Adapter> _adapters = new();
    // Extra LUIDs that are just another view of a real card (see EnumGpus) -> that card's LUID.
    readonly Dictionary<ulong, ulong> _alias = new();
    // Engine LUIDs DXGI still doesn't list after a re-enumeration (e.g. an NPU): not a GPU we can
    // name, so stop re-enumerating on their account.
    readonly HashSet<ulong> _unnamed = new();
    // Listed adapters that still report no engines after a re-enumeration: leave them be, rather
    // than re-enumerating on their account forever.
    readonly HashSet<ulong> _quiet = new();
    ulong Owner(ulong luid) => _alias.TryGetValue(luid, out var o) ? o : luid;
    int _gpuEnumAge;

    IntPtr _query, _cCpuTime, _cCpuPerf, _cCpuFreq, _cGpuEng, _cGpuDed, _cGpuShared;

    readonly Dictionary<string, (long rx, long tx)> _netPrev = new();
    long _netPrevTs;
    HashSet<string> _gatewayIds = new();
    int _gwAge = 30;   // due on the first sample (was int.MaxValue, whose ++ wrapped negative: never refreshed)

    void Init()
    {
        _inited = true;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            _cpuName = (k?.GetValue("ProcessorNameString") as string ?? "").Trim();
            if (k?.GetValue("~MHz") is int mhz) _cpuBaseMhzReg = mhz;
        }
        catch { }
        _cores = CountCores();
        EnumGpus();

        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
            // CPU = real work: "% Processor Time", the complement of System Idle. Deliberately NOT
            // Task Manager's "% Processor Utility", which counts time the cores are merely awake
            // (scaled by clock speed) - on a High-performance plan / with a hypervisor it reads
            // 70-90% while the machine is ~85% idle.
            _cCpuTime = AddCounter(@"\Processor Information(_Total)\% Processor Time");
            if (_cCpuTime == IntPtr.Zero) _cCpuTime = AddCounter(@"\Processor(_Total)\% Processor Time");
            _cCpuPerf = AddCounter(@"\Processor Information(_Total)\% Processor Performance");
            _cCpuFreq = AddCounter(@"\Processor Information(_Total)\Processor Frequency");
            _cGpuEng = AddCounter(@"\GPU Engine(*)\Utilization Percentage");
            _cGpuDed = AddCounter(@"\GPU Adapter Memory(*)\Dedicated Usage");
            _cGpuShared = AddCounter(@"\GPU Adapter Memory(*)\Shared Usage");
            PdhCollectQueryData(_query);   // prime: rate counters need two samples
        }
        catch { _query = IntPtr.Zero; }
    }

    IntPtr AddCounter(string path) =>
        PdhAddEnglishCounter(_query, path, IntPtr.Zero, out var c) == 0 ? c : IntPtr.Zero;

    MeterSnapshot? Sample()
    {
        if (!_inited) Init();
        if (_needPrime)
        {
            // Rate counters (CPU, GPU engines) and the network deltas need a baseline: take one now
            // and publish from the next tick, instead of a junk value computed over microseconds
            // (first sample) or averaged over a whole pause (resume).
            _needPrime = false;
            try { if (_query != IntPtr.Zero) PdhCollectQueryData(_query); } catch { }
            _netPrevTs = 0;
            _netPrev.Clear();
            try { SampleNetwork(new MeterSnapshot()); } catch { }
            return null;
        }
        var s = new MeterSnapshot
        {
            CpuName = _cpuName,
            Cores = _cores,
            Logical = Environment.ProcessorCount,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
        };

        try
        {
            if (_query != IntPtr.Zero && PdhCollectQueryData(_query) == 0)
            {
                s.Cpu = Math.Clamp(Value(_cCpuTime), 0, 100);
                double baseMhz = Value(_cCpuFreq);
                if (baseMhz <= 0) baseMhz = _cpuBaseMhzReg;
                s.CpuBaseGhz = baseMhz / 1000;
                double perf = Value(_cCpuPerf);   // current speed as % of base (Task Manager's "Speed")
                if (perf > 0 && baseMhz > 0) s.CpuGhz = baseMhz * perf / 100 / 1000;
                SampleGpus(s);
            }
        }
        catch { }

        try { SampleMemory(s); } catch { }
        try { SampleNetwork(s); } catch { }
        return s;
    }

    double Value(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return 0;
        return PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, IntPtr.Zero, out var v) == 0
               && v.CStatus is 0 or 1 ? v.DoubleValue : 0;
    }

    // Every instance of a wildcard counter, e.g. one per GPU engine per process.
    static List<(string Name, double Value)> Values(IntPtr counter)
    {
        var list = new List<(string, double)>();
        if (counter == IntPtr.Zero) return list;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = 0;
            uint rc = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);
            if (rc != PDH_MORE_DATA || size == 0) return list;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                rc = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, buf);
                if (rc == PDH_MORE_DATA) continue;   // instances grew between the two calls; ask again
                if (rc != 0) return list;
                // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, then the 8-aligned { CStatus, pad, double } -
                // so status at 8, value at 16, 24 bytes per item on 32- and 64-bit alike.
                const int stride = 24, statusAt = 8, valueAt = 16;
                for (int i = 0; i < count; i++)
                {
                    var item = buf + i * stride;
                    int status = Marshal.ReadInt32(item, statusAt);
                    if (status is not (0 or 1)) continue;
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    list.Add((name, BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, valueAt))));
                }
                return list;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return list;
    }

    // ---- GPU ---------------------------------------------------------------

    // Instance names look like "pid_1234_luid_0x00000000_0x0000C6E7_phys_0_eng_3_engtype_3D".
    static readonly Regex EngRx = new(@"luid_0x([0-9a-f]{1,8})_0x([0-9a-f]{1,8})_phys_(\d+)_eng_(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex LuidRx = new(@"luid_0x([0-9a-f]{1,8})_0x([0-9a-f]{1,8})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static ulong LuidKey(string hi, string lo) => (Convert.ToUInt64(hi, 16) << 32) | Convert.ToUInt32(lo, 16);

    void SampleGpus(MeterSnapshot s)
    {
        // Task Manager's GPU %: per engine, sum every process's share; the GPU is as busy as
        // its busiest engine (3D, copy, video decode, ...).
        var engines = new Dictionary<(ulong, string, string), double>();
        foreach (var (name, v) in Values(_cGpuEng))
        {
            var m = EngRx.Match(name);
            if (!m.Success) continue;
            var key = (LuidKey(m.Groups[1].Value, m.Groups[2].Value), m.Groups[3].Value, m.Groups[4].Value);
            engines[key] = engines.GetValueOrDefault(key) + v;
        }
        var util = new Dictionary<ulong, double>();
        foreach (var kv in engines)
        {
            var owner = Owner(kv.Key.Item1);   // a shadow LUID's work is that card's work
            util[owner] = Math.Max(util.GetValueOrDefault(owner), kv.Value);
        }

        // The adapter set changed (driver update/reset, eGPU plugged or unplugged, GPU disabled) when
        // a LUID we can't name shows up, or a listed one has stopped reporting engines - re-enumerate,
        // throttled so a permanent oddity can't make us do it every second.
        bool unmatched = false;
        foreach (var luid in util.Keys)   // keys are owners already
            if (!_unnamed.Contains(luid) && !_adapters.Exists(a => a.Luid == luid)) unmatched = true;
        if (util.Count > 0 && _adapters.Exists(a => !util.ContainsKey(a.Luid) && !_quiet.Contains(a.Luid)))
            unmatched = true;
        if ((unmatched || _adapters.Count == 0) && ++_gpuEnumAge >= 30)
        {
            _gpuEnumAge = 0;
            EnumGpus();
            foreach (var luid in util.Keys)
                if (!_alias.ContainsKey(luid) && !_adapters.Exists(a => a.Luid == luid)) _unnamed.Add(luid);
            if (util.Count > 0)
                foreach (var a in _adapters)
                    if (!util.ContainsKey(a.Luid)) _quiet.Add(a.Luid);
        }

        var ded = SumByLuid(Values(_cGpuDed), Owner);
        var shared = SumByLuid(Values(_cGpuShared), Owner);
        foreach (var a in _adapters)
        {
            var g = new GpuStat
            {
                Name = a.Name,
                DedicatedTotal = a.Dedicated,
                Util = Math.Clamp(util.GetValueOrDefault(a.Luid), 0, 100),
                DedicatedUsed = (ulong)Math.Max(0, ded.GetValueOrDefault(a.Luid)),
                SharedUsed = (ulong)Math.Max(0, shared.GetValueOrDefault(a.Luid)),
            };
            ReadPerfData(a, g);
            s.Gpus.Add(g);
            s.Gpu = Math.Max(s.Gpu, g.Util);   // the bar shows whichever GPU is busiest
        }
    }

    static Dictionary<ulong, double> SumByLuid(List<(string Name, double Value)> items, Func<ulong, ulong> owner)
    {
        var map = new Dictionary<ulong, double>();
        foreach (var (name, v) in items)
        {
            var m = LuidRx.Match(name);
            if (!m.Success) continue;
            var key = owner(LuidKey(m.Groups[1].Value, m.Groups[2].Value));
            map[key] = map.GetValueOrDefault(key) + v;
        }
        return map;
    }

    // Names, LUIDs and VRAM of the real (hardware) GPUs, via DXGI.
    //
    // One card can surface as more than one LUID: virtual-display drivers (VR streaming, "virtual
    // monitor" apps) add a second adapter view of the same GPU. That view has no PCI location, so
    // an adapter without one that shares a real card's model IDs is folded into that card -
    // otherwise a single RTX shows up twice. Two genuinely identical cards keep separate PCI
    // addresses and stay separate.
    void EnumGpus()
    {
        _adapters.Clear();
        _alias.Clear();
        _unnamed.Clear();   // re-derived by the caller against the fresh list
        _quiet.Clear();
        var found = new List<(Adapter A, string Model, bool Pci)>();
        IDXGIFactory1? factory = null;
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out factory) != 0 || factory == null) return;
            for (uint i = 0; factory.EnumAdapters1(i, out var ad) == 0 && ad != null; i++)
            {
                try
                {
                    if (ad.GetDesc1(out var d) != 0) continue;
                    if ((d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0 || d.VendorId == 0x1414) continue;   // Basic Render Driver
                    ulong key = ((ulong)(uint)d.LuidHigh << 32) | d.LuidLow;
                    if (found.Exists(x => x.A.Luid == key)) continue;
                    var a = new Adapter(key, d.LuidLow, d.LuidHigh, (d.Description ?? "").Trim(), (ulong)d.DedicatedVideoMemory);
                    found.Add((a, $"{d.VendorId:X4}:{d.DeviceId:X4}:{d.SubSysId:X8}", HasPciAddress(a)));
                }
                finally { Marshal.ReleaseComObject(ad); }
            }
        }
        catch { }
        finally { if (factory != null) Marshal.ReleaseComObject(factory); }

        foreach (var f in found)
        {
            var real = f.Pci ? default : found.Find(x => x.Pci && x.Model == f.Model);
            if (real.A != null) _alias[f.A.Luid] = real.A.Luid;
            else _adapters.Add(f.A);
        }
    }

    // False only when the kernel positively says the adapter has no PCI location (bus = ~0);
    // an unanswered query counts as real, so we never merge on a guess.
    static bool HasPciAddress(Adapter a)
    {
        try
        {
            var open = new D3DKMT_OPENADAPTERFROMLUID { LuidLow = a.LuidLow, LuidHigh = a.LuidHigh };
            if (D3DKMTOpenAdapterFromLuid(ref open) != 0) return true;
            var buf = Marshal.AllocHGlobal(12);   // D3DKMT_ADAPTERADDRESS { Bus, Device, Function }
            try
            {
                Marshal.WriteInt64(buf, 0, 0); Marshal.WriteInt32(buf, 8, 0);
                var q = new D3DKMT_QUERYADAPTERINFO
                {
                    hAdapter = open.hAdapter, Type = KMTQAITYPE_ADAPTERADDRESS,
                    pPrivateDriverData = buf, PrivateDriverDataSize = 12,
                };
                return D3DKMTQueryAdapterInfo(ref q) != 0 || (uint)Marshal.ReadInt32(buf, 0) != uint.MaxValue;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                var close = new D3DKMT_CLOSEADAPTER { hAdapter = open.hAdapter };
                D3DKMTCloseAdapter(ref close);
            }
        }
        catch { return true; }
    }

    // Temperature + fan, from the same kernel query Task Manager uses (WDDM 2.9+ drivers).
    static void ReadPerfData(Adapter a, GpuStat g)
    {
        try
        {
            var open = new D3DKMT_OPENADAPTERFROMLUID { LuidLow = a.LuidLow, LuidHigh = a.LuidHigh };
            if (D3DKMTOpenAdapterFromLuid(ref open) != 0) return;
            int size = Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>();
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(new D3DKMT_ADAPTER_PERFDATA(), buf, false);   // PhysicalAdapterIndex = 0
                var q = new D3DKMT_QUERYADAPTERINFO
                {
                    hAdapter = open.hAdapter,
                    Type = KMTQAITYPE_ADAPTERPERFDATA,
                    pPrivateDriverData = buf,
                    PrivateDriverDataSize = (uint)size,
                };
                if (D3DKMTQueryAdapterInfo(ref q) == 0)
                {
                    var pd = Marshal.PtrToStructure<D3DKMT_ADAPTER_PERFDATA>(buf);
                    double t = pd.Temperature / 10.0;   // deci-Celsius
                    if (t > 0 && t < 150) g.TempC = t;
                    g.FanRpm = pd.FanRPM;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                var close = new D3DKMT_CLOSEADAPTER { hAdapter = open.hAdapter };
                D3DKMTCloseAdapter(ref close);
            }
        }
        catch { }
    }

    // ---- CPU / memory --------------------------------------------------------

    static int CountCores()
    {
        try
        {
            uint len = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
            if (len == 0) return 0;
            var buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len)) return 0;
                // Variable-size records, one per physical core; each starts { Relationship, Size }.
                int n = 0;
                for (uint off = 0; off < len; n++)
                {
                    uint sz = (uint)Marshal.ReadInt32(buf, (int)off + 4);
                    if (sz == 0) break;
                    off += sz;
                }
                return n;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return 0; }
    }

    static void SampleMemory(MeterSnapshot s)
    {
        var pi = new PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>() };
        if (!K32GetPerformanceInfo(ref pi, pi.cb)) return;
        ulong page = (ulong)pi.PageSize;
        s.MemTotal = (ulong)pi.PhysicalTotal * page;
        s.MemAvail = (ulong)pi.PhysicalAvailable * page;
        s.MemCached = (ulong)pi.SystemCache * page;
        s.CommitUsed = (ulong)pi.CommitTotal * page;
        s.CommitLimit = (ulong)pi.CommitLimit * page;
        s.Processes = pi.ProcessCount;
        s.Threads = pi.ThreadCount;
        s.Handles = pi.HandleCount;
        if (s.MemTotal > 0) s.Mem = (s.MemTotal - s.MemAvail) * 100.0 / s.MemTotal;
    }

    // ---- network -------------------------------------------------------------

    // Bar = link utilisation (throughput / link speed) of the busiest adapter, as Task Manager's
    // Network column reports it. The tooltip names the busiest adapter, or the one carrying the
    // default route when everything is idle.
    void SampleNetwork(MeterSnapshot s)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = _netPrevTs == 0 ? 0 : (now - _netPrevTs) / (double)Stopwatch.Frequency;
        _netPrevTs = now;

        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); } catch { return; }
        if (++_gwAge >= 30) { _gwAge = 0; RefreshGateways(nics); }

        var seen = new Dictionary<string, (long rx, long tx)>();
        double bestRate = -1;
        bool bestGw = false;
        foreach (var ni in nics)
        {
            try
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var st = ni.GetIPStatistics();
                long rx = st.BytesReceived, tx = st.BytesSent;
                seen[ni.Id] = (rx, tx);

                double rxBps = 0, txBps = 0;
                if (dt > 0 && _netPrev.TryGetValue(ni.Id, out var p))
                {
                    rxBps = Math.Max(0, rx - p.rx) * 8 / dt;
                    txBps = Math.Max(0, tx - p.tx) * 8 / dt;
                }
                long speed = ni.Speed;
                if (speed > 0) s.Net = Math.Max(s.Net, Math.Min(100, (rxBps + txBps) * 100.0 / speed));

                double rate = rxBps + txBps;
                bool gw = _gatewayIds.Contains(ni.Id);
                if (rate > bestRate || (rate == bestRate && gw && !bestGw))
                {
                    bestRate = rate; bestGw = gw;
                    s.NetName = ni.Name;
                    s.NetSendBps = txBps;
                    s.NetRecvBps = rxBps;
                    s.NetLinkBps = Math.Max(0, speed);
                }
            }
            catch { }
        }
        _netPrev.Clear();
        foreach (var kv in seen) _netPrev[kv.Key] = kv.Value;
    }

    void RefreshGateways(NetworkInterface[] nics)
    {
        var ids = new HashSet<string>();
        foreach (var ni in nics)
        {
            try
            {
                foreach (var g in ni.GetIPProperties().GatewayAddresses)
                    if (!g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)) { ids.Add(ni.Id); break; }
            }
            catch { }
        }
        _gatewayIds = ids;
    }

    // ---- interop ---------------------------------------------------------------

    const uint PDH_FMT_DOUBLE = 0x00000200, PDH_FMT_NOCAP100 = 0x00008000, PDH_MORE_DATA = 0x800007D2;
    const int RelationProcessorCore = 0;
    const int KMTQAITYPE_ADAPTERADDRESS = 6, KMTQAITYPE_ADAPTERPERFDATA = 62;
    const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

    [StructLayout(LayoutKind.Explicit)]
    struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")]
    static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out PDH_FMT_COUNTERVALUE value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr items);
    [DllImport("pdh.dll")]
    static extern uint PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Sequential)]
    struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
                       SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("kernel32.dll")]
    static extern bool K32GetPerformanceInfo(ref PERFORMANCE_INFORMATION info, uint cb);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_OPENADAPTERFROMLUID { public uint LuidLow; public int LuidHigh; public uint hAdapter; }
    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pPrivateDriverData; public uint PrivateDriverDataSize; }
    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }
    [StructLayout(LayoutKind.Sequential)]
    struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOC, MemoryBandwidth, PCIEBandwidth;
        public uint FanRPM, Power, Temperature;
        public byte PowerStateOverride;
    }

    [DllImport("gdi32.dll")] static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID open);
    [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO query);
    [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER close);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    // COM vtables, flattened (classic COM interop doesn't inherit base-interface slots). Only
    // EnumAdapters1 / GetDesc1 are called; the rest are placeholders that keep the slots aligned.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIFactory1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation();
        void CreateSwapChain(); void CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDXGIAdapter1
    {
        void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
        void EnumOutputs(); void GetDesc(); void CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }

    [DllImport("dxgi.dll")]
    static extern int CreateDXGIFactory1(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IDXGIFactory1 factory);
}
