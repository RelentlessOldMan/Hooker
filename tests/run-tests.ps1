# Hooker scenario tests: builds isolated TEST copies of hook.exe and the widget, then runs the
# scenario harness (tests\Harness) against them. Safe to run next to a live Hooker: the test
# copies use their own home folder (registry + sessions), their own widget mutex, and a hidden
# widget, so the installed Hooker and Claude's real session registry are never touched.
#
#   powershell -ExecutionPolicy Bypass -File .\tests\run-tests.ps1                # all scenarios
#   powershell -ExecutionPolicy Bypass -File .\tests\run-tests.ps1 -Filter clear  # names containing "clear"
#   powershell -ExecutionPolicy Bypass -File .\tests\run-tests.ps1 -SelfCheck     # prove the tests catch old bugs
#
# -SelfCheck puts each past bug back into a copy of the code, one at a time, and checks that
# the scenario written for it now FAILS. A bug the tests no longer catch is reported as MISSED.
param([string]$Filter, [switch]$SelfCheck)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$work = Join-Path $env:TEMP 'hooker-tests'

# Replace text in one source file; it must match exactly $count times, so a refactor that moves
# these lines fails loudly here instead of silently testing the wrong thing.
function Edit-Src($dir, $file, $find, $replace, [int]$count = 1) {
    $path = Join-Path $dir $file
    $text = [IO.File]::ReadAllText($path)
    $n = ([regex]::Matches($text, [regex]::Escape($find))).Count
    if ($n -ne $count) { throw "expected $count x '$find' in $file, found $n - update run-tests.ps1" }
    [IO.File]::WriteAllText($path, $text.Replace($find, $replace))
}

# A copy of the sources pointed at the test home, hidden, with the test probe added.
function New-TestSrc($dir) {
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item (Join-Path $repo 'Directory.Build.props'), (Join-Path $repo 'VERSION') $dir
    Copy-Item (Join-Path $repo 'assets') $dir -Recurse
    foreach ($p in 'shim', 'tray') {
        New-Item -ItemType Directory -Force (Join-Path $dir $p) | Out-Null
        Get-ChildItem (Join-Path $repo $p) -File | Copy-Item -Destination (Join-Path $dir $p)
    }
    $userHome = 'Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)'
    $testHome = 'Environment.GetEnvironmentVariable("HOOKER_TEST_HOME")!'
    Edit-Src $dir 'shim\Program.cs' $userHome $testHome 2
    Edit-Src $dir 'shim\Program.cs' '"Hooker.Widget.SingleInstance"' '"Hooker.Widget.Test.Harness"'
    Edit-Src $dir 'tray\Program.cs' $userHome $testHome 2
    Edit-Src $dir 'tray\Program.cs' '"Hooker.Widget.SingleInstance"' '"Hooker.Widget.Test.Harness"'
    # Hidden (no window pops up); after every sync, report the tiles and take any test clicks.
    Edit-Src $dir 'tray\Program.cs' "bool ShouldHideForFullscreen()`n    {" "bool ShouldHideForFullscreen()`n    {`n        if (Environment.GetEnvironmentVariable(`"HOOKER_TEST_HIDDEN`") == `"1`") return true;"
    Edit-Src $dir 'tray\Program.cs' "            SyncSessions();`n`n            // Poll the display topology" "            SyncSessions(); TestProbe.Dump(_order, _sessions); TestProbe.Run(ToggleSession, DismissSession);`n`n            // Poll the display topology"
    Add-Content (Join-Path $dir 'tray\Program.cs') @'

// Test build only (added by tests\run-tests.ps1). Dump: the tiles shown, left to right, one per
// line: "<tile> on|off working|waiting <count>". Run: clicks asked for in cmd.txt, one per line:
// "toggle <tile>" (left-click) or "dismiss <tile>".
static class TestProbe
{
    public static void Run(Action<string> toggle, Action<string> dismiss)
    {
        try
        {
            var path = Path.Combine(Environment.GetEnvironmentVariable("HOOKER_TEST_HOME")!, "cmd.txt");
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path);
            File.Delete(path);
            foreach (var line in lines)
            {
                var p = line.Split(' ');
                if (p.Length != 2) continue;
                if (p[0] == "toggle") toggle(p[1]); else if (p[0] == "dismiss") dismiss(p[1]);
            }
        }
        catch { }
    }

    public static void Dump(List<string> order, Dictionary<string, Session> sessions)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var t in order)
            {
                sessions.TryGetValue(t, out var s);
                sb.Append(t).Append(s?.Hooking == true ? " on" : " off").Append(s?.Working == true ? " working " : " waiting ")
                  .Append(s?.Count ?? 0).Append('\n');
            }
            var path = Path.Combine(Environment.GetEnvironmentVariable("HOOKER_TEST_HOME")!, "tiles.now");
            File.WriteAllText(path + ".tmp", sb.ToString());
            File.Move(path + ".tmp", path, true);
        }
        catch { }
    }
}
'@
}

function Build($proj, $out) {
    $log = dotnet build $proj -c Release -o $out --nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) { $log | Write-Host; throw "build failed: $proj" }
}

function Run-Harness($bin, $filter) {
    $a = @('--hook', (Join-Path $bin 'shim\hook.exe'), '--widget', (Join-Path $bin 'tray\HookerWidget.exe'), '--work', (Join-Path $work 'run'))
    if ($filter) { $a += @('--filter', $filter) }
    & (Join-Path $work 'harness\HookerTests.exe') @a
}

$src = Join-Path $work 'src'
$bin = Join-Path $work 'bin'
New-TestSrc $src
Remove-Item $bin -Recurse -Force -ErrorAction SilentlyContinue
Build (Join-Path $src 'shim\Shim.csproj') (Join-Path $bin 'shim')
Build (Join-Path $src 'tray\Tray.csproj') (Join-Path $bin 'tray')
Build (Join-Path $PSScriptRoot 'Harness\Harness.csproj') (Join-Path $work 'harness')

if (-not $SelfCheck) {
    Run-Harness $bin $Filter
    exit $LASTEXITCODE
}

# Each past bug, put back: file, the fixed code, the buggy code, and the scenario that must fail.
$bugs = @(
    @{ Name = 'hook attaches to any listed ancestor (pre-a6f98aa)'; File = 'shim\Program.cs'
       Find = 'if (Read(f) is (string s, long p) && s == sid) pids.Add(p);'; Bug = 'if (Read(f) is (string s, long p)) pids.Add(p);'
       Scenario = 'started inside another' },
    @{ Name = 'hook falls back to any window of its session (closing one wiped the other)'; File = 'shim\Program.cs'
       Find = "                if (pids.Contains(pid)) return pid;`n            }`n            return 0;"; Bug = "                if (pids.Contains(pid)) return pid;`n            }`n            return pids.First();"
       Scenario = 'closing one of two windows' },
    @{ Name = 'one tile per conversation, not per window (pre-7989628)'; File = 'shim\Program.cs'
       Find = 'var tile = owner > 0 ? Sanitize(sid) + "@" + owner : Sanitize(sid);'; Bug = 'var tile = Sanitize(sid);'
       Scenario = 'two windows' },
    @{ Name = 'a session gone from the registry keeps its tile (72a980d)'; File = 'tray\Program.cs'
       Find = 'bool missing = regUsable && !reg.ContainsKey(sid)'; Bug = 'bool missing = false && !reg.ContainsKey(sid)'
       Scenario = 'drops the session' },
    @{ Name = 'hook ignores whether the widget is running'; File = 'shim\Program.cs'
       Find = 'if (!Mutex.TryOpenExisting("Hooker.Widget.Test.Harness", out var widget)) return false;'; Bug = 'if (!Mutex.TryOpenExisting("Hooker.Widget.Test.Harness", out var widget)) return true;'
       Scenario = "widget isn't running" },
    @{ Name = 'hook reads only <pid>.json (shared-process sessions lose autopilot)'; File = 'shim\Program.cs'
       Find = 'foreach (var f in Directory.GetFiles(registryDir, "*.json"))'; Bug = 'foreach (var f in Directory.GetFiles(registryDir, "*.json").Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"^\d+\.json$")))'
       Scenario = 'several sessions in one' },
    @{ Name = 'stale hook status beats Claude''s live status (dd24118)'; File = 'tray\Program.cs'
       Find = 'public bool Working => LiveStatus.Length > 0 ? LiveStatus == "busy"'; Bug = 'public bool Working => false ? LiveStatus == "busy"'
       Scenario = 'live status' },
    @{ Name = 'claude -r picker gets a tile (06faeda)'; File = 'tray\Program.cs'
       Find = 'pre.Status.Length == 0) continue;'; Bug = 'pre.Status.Length < 0) continue;'
       Scenario = 'picker' },
    @{ Name = 'killed Claude keeps its tile (06faeda)'; File = 'tray\Program.cs'
       Find = 'if (pid > 0 && !ProcessAlive(pid, pidSeen)) continue;'; Bug = ''
       Scenario = 'killed Claude' },
    @{ Name = 'sessions sharing a process seen as /clear swaps (68e8f83: both its guards)'; File = 'tray\Program.cs'
       Find = 'if (procCount[proc] > 1) continue;'; Bug = ''
       Find2 = "`n                && !reg.ContainsKey(old))"; Bug2 = ')'
       Scenario = 'several sessions in one' },
    @{ Name = 'widget start doesn''t wipe switches (08dcbfb)'; File = 'tray\Program.cs'
       Find = "internal static void WipeStates()`n    {"; Bug = "internal static void WipeStates()`n    {`n        return;"
       Scenario = "widget isn't running" },
    @{ Name = '/clear drops autopilot (3f1a72e)'; File = 'tray\Program.cs'
       Find = 'if (fresh && start == "clear" && sw.WasOn && st == null)'; Bug = 'if (false && fresh && start == "clear" && sw.WasOn && st == null)'
       Scenario = '/clear keeps the tile' },
    @{ Name = 'a torn registry read forgets the process before /clear'; File = 'tray\Program.cs'
       Find = 'if (n >= RegMissesToPrune) { _procSid.Remove(proc);'; Bug = 'if (n >= 1) { _procSid.Remove(proc);'
       Scenario = 'tore just before' },
    @{ Name = 'Remember restores per conversation, not per window'; File = 'tray\Program.cs'
       Find = 'bool restore = _knownTiles.Contains(sid) ? _lastOn.Contains(sid) : _rememberedAtStart.Contains(sid);'; Bug = 'bool restore = _remembered.ContainsKey(SidOf(sid));'
       Scenario = 'each window gets its own' },
    @{ Name = 'saved on-windows count as on without Remember'; File = 'tray\Program.cs'
       Find = '                        foreach (var sid in c.OnTiles ?? new List<string>())'; Bug = "                        }`n                        {`n                        foreach (var sid in c.OnTiles ?? new List<string>())"
       Scenario = 'without Remember' },
    @{ Name = 'no miss budget: one torn read evicts a tile'; File = 'tray\Program.cs'
       Find = 'const int RegMissesToPrune = 15;'; Bug = 'const int RegMissesToPrune = 1;'
       Scenario = 'tore just before' }
)

$missed = 0
foreach ($b in $bugs) {
    $mdir = Join-Path $work 'mutant'
    Remove-Item $mdir -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item $src $mdir -Recurse
    Edit-Src $mdir $b.File $b.Find $b.Bug
    if ($b.Find2) { Edit-Src $mdir $b.File $b.Find2 $b.Bug2 }
    $mbin = Join-Path $work 'mutant-bin'
    Remove-Item $mbin -Recurse -Force -ErrorAction SilentlyContinue
    Copy-Item $bin $mbin -Recurse
    $proj = if ($b.File -like 'shim*') { 'shim\Shim.csproj' } else { 'tray\Tray.csproj' }
    Build (Join-Path $mdir $proj) (Join-Path $mbin (Split-Path $proj -Parent))
    $out = Run-Harness $mbin $b.Scenario
    if ($LASTEXITCODE -ne 0) { Write-Host "caught   $($b.Name)" }
    else { $missed++; Write-Host "MISSED   $($b.Name)"; $out | Write-Host }
}
Write-Host "`n$($bugs.Count - $missed)/$($bugs.Count) old bugs caught"
exit $(if ($missed -eq 0) { 0 } else { 1 })
