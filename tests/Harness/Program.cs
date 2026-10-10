// Hooker scenario tests. Drives TEST builds of the real hook.exe and widget through the session
// sequences that have broken before, and checks the tiles the widget actually shows.
//
// run-tests.ps1 builds those test copies: both read HOOKER_TEST_HOME instead of the user profile
// (so they use an isolated registry + sessions folder) and a test-only widget mutex, so the
// installed Hooker and the real Claude registry are never touched. The test widget stays hidden
// and writes the tiles it shows to <home>\tiles.now each tick.
//
// A "fake Claude" is this exe in fake-claude mode: a live process listed in the test registry
// that runs hook.exe as its descendant through cmd.exe, the way Claude runs hooks via a shell.
//
//   HookerTests --hook <hook.exe> --widget <HookerWidget.exe> --work <dir> [--filter <text>]

using System.Diagnostics;
using System.Text.Json;

if (args.Length > 0 && args[0] == "fake-claude") return FakeClaudeMode.Run();

string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
if (Arg("--hook") is not string hook || Arg("--widget") is not string widget || Arg("--work") is not string work)
{
    Console.Error.WriteLine("usage: HookerTests --hook <hook.exe> --widget <HookerWidget.exe> --work <dir> [--filter <text>]");
    return 2;
}
var env = new Env(Path.GetFullPath(hook), Path.GetFullPath(widget), Path.GetFullPath(work));
var filter = Arg("--filter");
int ran = 0, failed = 0, skipped = 0;
foreach (var (name, body) in Scenarios.All(env))
{
    if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
    ran++;
    var sw = Stopwatch.StartNew();
    try
    {
        env.Reset();
        body();
        Console.WriteLine($"PASS  {name}  ({sw.Elapsed.TotalSeconds:F1}s)");
    }
    catch (SkipException ex)
    {
        skipped++;
        Console.WriteLine($"SKIP  {name}\n      {ex.Message}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}  ({sw.Elapsed.TotalSeconds:F1}s)\n      {ex.Message}");
        Console.WriteLine("      tiles now: " + env.Describe());
        Console.WriteLine("      files:     " + string.Join(" ", env.SessionFiles()));
    }
    finally { env.Teardown(); }
}
if (ran == 0) { Console.WriteLine($"no scenario matches '{filter}'"); return 3; }
Console.WriteLine($"\n{ran - failed - skipped}/{ran} passed" + (skipped > 0 ? $", {skipped} skipped" : ""));
return failed == 0 ? 0 : 1;

// A scenario that couldn't set itself up on this machine (not a pass, not a failure).
sealed class SkipException(string why) : Exception(why);

// Every sequence here is one that broke before (commit in brackets) or a case a fix relies on.
static class Scenarios
{
    static string Sid() => Guid.NewGuid().ToString();
    static string T(string sid, Fake f) => sid + "@" + f.Pid;

    public static List<(string, Action)> All(Env e) => new()
    {
        ("fresh session gets one tile, manual", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var s = Sid();
            e.Start(a, s);
            e.Until(() => e.Has(T(s, a)), "tile shown");
            e.Expect(!e.On(T(s, a)) && e.Tiles().Count == 1, "exactly one tile, off");
            e.Expect(File.Exists(e.Meta(T(s, a))) && !File.Exists(e.Meta(s)), "status file is per window, not bare");
        }),

        ("picker placeholder shows no tile until a session runs [06faeda]", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var s = Sid();
            a.Register(s, status: "");
            e.Hold(() => e.Tiles().Count == 0, 1500, "no tile while claude -r's picker is up");
            a.Register(s);
            a.Hook("SessionStart", s, source: "resume");
            e.Until(() => e.Has(T(s, a)), "tile once picked");
        }),

        ("Claude's live status beats a stale hook status [dd24118]", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var s = Sid();
            e.Start(a, s);
            a.Register(s, status: null);                  // registry silent: the hook's status shows
            a.Hook("UserPromptSubmit", s);
            e.Until(() => e.Tile(T(s, a))?.Working == true, "hook 'working' used as fallback");
            a.Register(s, status: "idle");                // the hook is stuck at working, Claude says idle
            e.Until(() => e.Tile(T(s, a))?.Working == false, "idle wins over a stuck 'working'");
            a.Register(s, status: "busy");
            e.Until(() => e.Tile(T(s, a))?.Working == true, "busy shows working");
            a.Register(s, status: null);
            a.Hook("Stop", s);
            e.Until(() => e.Tile(T(s, a))?.Working == false, "hook 'waiting' used as fallback");
        }),

        ("tile goes when Claude drops the session from its registry [72a980d]", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var s = Sid();
            e.Start(a, s); e.SetOn(T(s, a));
            e.Until(() => e.On(T(s, a)), "on");
            a.Unregister();
            e.Until(() => !e.Has(T(s, a)), "evicted", 5000);
            e.Expect(!File.Exists(e.Meta(T(s, a))) && !File.Exists(e.State(T(s, a))), "its files (incl. an 'on' switch) deleted");
        }),

        ("killed Claude's tile goes though its registry file stays [06faeda]", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var s = Sid();
            e.Start(a, s);
            e.Until(() => e.Has(T(s, a)), "tile shown");
            a.Kill();
            e.Until(() => !e.Has(T(s, a)), "evicted", 5000);
        }),

        ("idle session's tile stays, whatever its procStart says [1c6e33a]", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var s = Sid();
            a.Register(s, procStart: "1");
            a.Hook("SessionStart", s, source: "startup");
            e.Until(() => e.Has(T(s, a)), "tile shown");
            e.Hold(() => e.Has(T(s, a)), 4000, "idle tile stays");
        }),

        ("widget started after sessions shows them all", () =>
        {
            var a = e.NewClaude(); var b = e.NewClaude(); string s1 = Sid(), s2 = Sid();
            e.Start(a, s1); e.Start(b, s2);
            e.StartWidget();
            e.Until(() => e.Has(T(s1, a)) && e.Has(T(s2, b)) && e.Tiles().Count == 2, "both tiles");
        }),

        ("same conversation in two windows: two tiles, separate autopilot [7989628]", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var b = e.NewClaude(); var s = Sid();
            e.Start(a, s); e.Start(b, s, "resume");
            e.Until(() => e.Has(T(s, a)) && e.Has(T(s, b)), "a tile per window");
            e.SetOn(T(s, b));
            e.Until(() => e.On(T(s, b)), "b on");
            e.Expect(!e.On(T(s, a)), "a stays off");
            e.Expect(!e.Tool(a, s), "a's tool call still prompts");
            e.Expect(e.Tool(b, s), "b's tool call auto-approved");
        }),

        ("closing one of two windows on a conversation keeps the other's tile [7989628]", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var b = e.NewClaude(); var s = Sid();
            e.Start(a, s); e.Start(b, s, "resume");
            e.Until(() => e.Has(T(s, a)) && e.Has(T(s, b)), "a tile per window");
            e.SetOn(T(s, b));
            e.Until(() => e.On(T(s, b)), "b on");
            e.Close(a, s);
            e.Until(() => !e.Has(T(s, a)), "closed window's tile gone", 5000);
            e.Hold(() => e.Has(T(s, b)) && e.On(T(s, b)), 2500, "survivor keeps its tile and autopilot");
            e.Expect(File.Exists(e.Meta(T(s, b))), "survivor's status file intact");
            e.Expect(e.Tool(b, s), "survivor still auto-approves");
            e.Close(b, s);
            e.Until(() => e.Tiles().Count == 0, "last tile gone", 5000);
            e.Until(() => e.SessionFiles().Count == 0, "no files left behind", 5000);
        }),

        ("closing the window that's on leaves the other one manual [7989628]", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var b = e.NewClaude(); var s = Sid();
            e.Start(a, s); e.Start(b, s, "resume");
            e.Until(() => e.Has(T(s, a)) && e.Has(T(s, b)), "a tile per window");
            e.SetOn(T(s, a));
            e.Until(() => e.On(T(s, a)), "a on");
            e.Close(a, s);
            e.Until(() => !e.Has(T(s, a)), "a's tile gone", 5000);
            e.Hold(() => e.Has(T(s, b)) && !e.On(T(s, b)), 2000, "b present and still manual");
            e.Expect(!e.Tool(b, s), "b still prompts");
        }),

        ("killing one of two windows on a conversation keeps the other's tile", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var b = e.NewClaude(); var s = Sid();
            e.Start(a, s); e.Start(b, s, "resume");
            e.Until(() => e.Has(T(s, a)) && e.Has(T(s, b)), "a tile per window");
            e.SetOn(T(s, b));
            e.Until(() => e.On(T(s, b)), "b on");
            a.Kill();
            e.Until(() => !e.Has(T(s, a)), "killed window's tile gone", 5000);
            e.Hold(() => e.Has(T(s, b)) && e.On(T(s, b)), 2000, "survivor keeps tile and autopilot");
        }),

        ("/clear keeps the tile's slot and autopilot [3f1a72e]", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var x = e.NewClaude(); string s1 = Sid(), s2 = Sid(), sx = Sid();
            e.Start(a, s1);
            e.Until(() => e.Has(T(s1, a)), "first tile");
            e.Start(x, sx);
            e.Until(() => e.Has(T(sx, x)), "second tile");
            e.SetOn(T(s1, a));
            e.Until(() => e.On(T(s1, a)), "on");
            a.Hook("SessionEnd", s1);                     // Claude: the old id ends...
            a.Register(s2);                               // ...the same process re-lists under the new id...
            a.Hook("SessionStart", s2, source: "clear");  // ...and starts it
            e.Until(() => e.Has(T(s2, a)) && e.On(T(s2, a)) && !e.Has(T(s1, a)), "new id's tile, still on");
            e.Expect(e.Tiles()[0].Id == T(s2, a), "kept its slot (first)");
            e.Expect(e.Tool(a, s2), "auto-approves under the new id");
        }),

        ("/clear when the registry updates before SessionEnd also keeps autopilot", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); string s1 = Sid(), s2 = Sid();
            e.Start(a, s1); e.SetOn(T(s1, a));
            e.Until(() => e.On(T(s1, a)), "on");
            a.Register(s2);
            a.Hook("SessionEnd", s1);
            a.Hook("SessionStart", s2, source: "clear");
            e.Until(() => e.Has(T(s2, a)) && e.On(T(s2, a)) && !e.Has(T(s1, a)), "new id's tile, still on");
        }),

        ("/resume inside a window starts the other conversation manual [3f1a72e]", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); string s1 = Sid(), s2 = Sid();
            e.Start(a, s1); e.SetOn(T(s1, a));
            e.Until(() => e.On(T(s1, a)), "on");
            a.Hook("SessionEnd", s1);
            a.Register(s2);
            a.Hook("SessionStart", s2, source: "resume");
            e.Until(() => e.Has(T(s2, a)) && !e.Has(T(s1, a)), "swapped tile");
            e.Hold(() => !e.On(T(s2, a)), 2000, "manual");
            e.Expect(!e.Tool(a, s2), "prompts");
        }),

        ("resuming a conversation starts manual", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var s = Sid();
            e.Start(a, s); e.SetOn(T(s, a));
            e.Until(() => e.On(T(s, a)), "on");
            e.Close(a, s);
            var b = e.NewClaude();
            e.Start(b, s, "resume");
            e.Until(() => e.Has(T(s, b)), "resumed tile");
            e.Hold(() => !e.On(T(s, b)), 2000, "manual");
        }),

        ("Remember autopilot turns a resumed conversation back on [0bcfbbe]", () =>
        {
            string s = Sid(), other = Sid();
            e.WriteConfig(remember: true, autopilot: new() { [s] = Env.NowMs() });
            e.StartWidget();
            var b = e.NewClaude(); var c = e.NewClaude();
            e.Start(b, s, "resume"); e.Start(c, other, "resume");
            e.Until(() => e.On(T(s, b)) && e.Has(T(other, c)), "remembered conversation back on");
            e.Hold(() => e.Has(T(other, c)) && !e.On(T(other, c)), 1500, "other conversation stays manual");
        }),

        ("hook of a Claude started inside another doesn't attach to the outer window [a6f98aa]", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); string s = Sid(), inner = Sid();
            e.Start(a, s); e.SetOn(T(s, a));
            e.Until(() => e.On(T(s, a)), "outer on");
            a.Hook("SessionStart", inner, source: "startup");   // runs under a, but a lists s
            e.Expect(!e.Tool(a, inner), "inner session's tool call prompts");
            e.Expect(!File.Exists(e.Meta(T(inner, a))) && !File.Exists(e.State(T(inner, a))), "nothing written under the outer window's pid");
            e.Expect(e.On(T(s, a)) && e.Tool(a, s), "outer still on and approving");
        }),

        ("a dead Claude's registry entry on the hook's shell pid is skipped [a6f98aa]", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var s = Sid();
            e.Start(a, s); e.SetOn(T(s, a));
            e.Until(() => e.On(T(s, a)), "on");
            e.Expect(a.HookStale("PreToolUse", s, staleSid: Sid()).Contains("\"allow\""), "still attached to the real window");
            e.Until(() => e.Tile(T(s, a))?.Count == 1, "approval counted on the right tile");
        }),

        ("several sessions in one Claude process each get a working tile [68e8f83]", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); string s1 = Sid(), s2 = Sid();
            a.Register(s1);
            a.Register(s2, file: a.Pid + "-2.json");
            a.Hook("SessionStart", s1, source: "startup");
            a.Hook("SessionStart", s2, source: "startup");
            e.Until(() => e.Has(T(s1, a)) && e.Has(T(s2, a)), "a tile per session");
            e.SetOn(T(s2, a));
            e.Until(() => e.On(T(s2, a)), "s2 on");
            e.Expect(e.Tool(a, s2), "s2 auto-approves");
            e.Expect(!e.Tool(a, s1), "s1 still prompts");
            e.Hold(() => e.Has(T(s1, a)) && e.Has(T(s2, a)), 2000, "neither evicted");
        }),

        ("no auto-approve while the widget isn't running; restart starts manual [08dcbfb]", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var s = Sid();
            e.Start(a, s); e.SetOn(T(s, a));
            e.Until(() => e.On(T(s, a)), "on");
            e.Expect(e.Tool(a, s), "approves while running");
            e.KillWidget();
            e.Expect(!e.Tool(a, s), "a crashed widget grants nothing");
            e.StartWidget();
            e.Until(() => e.Has(T(s, a)), "tile back");
            e.Hold(() => e.Has(T(s, a)) && !e.On(T(s, a)), 1500, "back manual");
            e.Expect(!e.Tool(a, s), "prompts");
        }),

        ("parallel tool calls are each counted", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var s = Sid();
            e.Start(a, s); e.SetOn(T(s, a));
            e.Until(() => e.On(T(s, a)), "on");
            a.HookParallel(8, "PreToolUse", s);
            e.Until(() => e.Tile(T(s, a))?.Count == 8, "8 approvals counted");
        }),

        ("Remember: after a widget restart each window gets its own setting back", () =>
        {
            e.WriteConfig(remember: true);
            e.StartWidget();
            var a = e.NewClaude(); var b = e.NewClaude(); var c = e.NewClaude(); var s = Sid();
            e.Start(a, s); e.Start(b, s, "resume"); e.Start(c, s, "resume");
            e.Until(() => e.Has(T(s, a)) && e.Has(T(s, b)) && e.Has(T(s, c)), "a tile per window");
            e.Click(T(s, a));                              // a: on
            e.Click(T(s, b)); e.Click(T(s, b));            // b: turned on, then explicitly off
            e.Until(() => e.On(T(s, a)) && !e.On(T(s, b)) && !e.On(T(s, c)), "a on, b and c manual");
            e.KillWidget();
            e.StartWidget();
            e.Until(() => e.On(T(s, a)) && e.Has(T(s, b)) && e.Has(T(s, c)), "a back on");
            e.Hold(() => e.Has(T(s, b)) && e.Has(T(s, c)) && !e.On(T(s, b)) && !e.On(T(s, c)), 2500, "b and c stay manual");
            e.Expect(!e.Tool(b, s) && !e.Tool(c, s), "b and c prompt");
        }),

        ("Remember: a dismissed window doesn't come back on after a widget restart", () =>
        {
            e.WriteConfig(remember: true);
            e.StartWidget();
            var a = e.NewClaude(); var b = e.NewClaude(); var s = Sid();
            e.Start(a, s); e.Start(b, s, "resume");
            e.Until(() => e.Has(T(s, a)) && e.Has(T(s, b)), "a tile per window");
            e.Click(T(s, a)); e.Click(T(s, b));
            e.Until(() => e.On(T(s, a)) && e.On(T(s, b)), "both on");
            e.Dismiss(T(s, b));
            e.Until(() => !e.Has(T(s, b)), "b dismissed");
            e.KillWidget();
            e.StartWidget();
            e.Until(() => e.On(T(s, a)), "a back on");
            e.Hold(() => !e.Has(T(s, b)), 2000, "b still dismissed after the restart");
            b.Hook("UserPromptSubmit", s);                 // b acts again: its tile returns...
            e.Until(() => e.Has(T(s, b)), "b's tile back");
            e.Hold(() => e.Has(T(s, b)) && !e.On(T(s, b)), 2000, "...manual");
            e.Expect(!e.Tool(b, s), "b prompts");
        }),

        ("without Remember, a window on before a widget restart stays manual through /clear", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); string s1 = Sid(), s2 = Sid();
            e.Start(a, s1);
            e.Until(() => e.Has(T(s1, a)), "tile");
            e.Click(T(s1, a));
            e.Until(() => e.On(T(s1, a)), "on");
            e.KillWidget();
            e.StartWidget();
            e.Until(() => e.Has(T(s1, a)), "tile back");
            e.Hold(() => e.Has(T(s1, a)) && !e.On(T(s1, a)), 1000, "back manual");
            a.Hook("SessionEnd", s1);
            a.Register(s2);
            a.Hook("SessionStart", s2, source: "clear");
            e.Until(() => e.Has(T(s2, a)) && !e.Has(T(s1, a)), "swapped tile");
            e.Hold(() => !e.On(T(s2, a)), 2000, "still manual");
        }),

        ("/clear keeps autopilot though a registry read tore just before", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var x = e.NewClaude(); string s1 = Sid(), s2 = Sid();
            e.Start(x, Sid());                             // another session keeps the registry readable
            e.Start(a, s1); e.SetOn(T(s1, a));
            e.Until(() => e.On(T(s1, a)), "on");
            // /clear: the widget catches Claude mid-rewrite of the entry, then sees the new id.
            File.WriteAllText(Path.Combine(e.Registry, a.Pid + ".json"), "{\"pid\":");
            e.WaitTicks(3);
            a.Hook("SessionEnd", s1);
            a.Register(s2);
            a.Hook("SessionStart", s2, source: "clear");
            e.Until(() => e.Has(T(s2, a)) && !e.Has(T(s1, a)), "swapped tile");
            e.Until(() => e.On(T(s2, a)), "still on");
        }),

        ("a new Claude that gets a killed Claude's pid has a tile", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var s = Sid();
            e.Start(a, s);
            e.Until(() => e.Has(T(s, a)), "tile shown");
            int pid = a.Pid;
            a.Kill();                                      // its registry file stays behind
            e.Until(() => !e.Has(T(s, a)), "killed tile gone", 5000);
            var b = e.NewClaudeWithPid(pid) ?? throw new SkipException("Windows didn't hand out the same pid again");
            var s2 = Sid();
            b.Register(s2);                                // overwrites the dead entry, no hook yet
            e.Until(() => e.Has(T(s2, b)), "new Claude's tile, from the registry alone");
        }),

        ("a killed Claude's pid taken by another process doesn't bring its tile back", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var s = Sid();
            e.Start(a, s);
            e.Until(() => e.Has(T(s, a)), "tile shown");
            int pid = a.Pid;
            a.Kill();                                      // its registry file stays behind
            e.Until(() => !e.Has(T(s, a)), "killed tile gone", 5000);
            _ = e.NewClaudeWithPid(pid) ?? throw new SkipException("Windows didn't hand out the same pid again");
            e.Hold(() => !e.Has(T(s, a)), 2000, "the dead window stays gone");   // not a Claude: never lists itself
        }),

        ("auto-compact keeps autopilot and the tally", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var s = Sid();
            e.Start(a, s); e.SetOn(T(s, a));
            e.Until(() => e.On(T(s, a)), "on");
            e.Expect(e.Tool(a, s) && e.Tool(a, s), "approves");
            e.Until(() => e.Tile(T(s, a))?.Count == 2, "2 approvals counted");
            a.Hook("SessionStart", s, source: "compact");
            e.Hold(() => e.On(T(s, a)) && e.Tile(T(s, a))?.Count == 2, 1500, "still on, tally kept");
            e.Expect(e.Tool(a, s), "still approves");
        }),

        ("AskUserQuestion is never auto-approved, even on autopilot", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var s = Sid();
            e.Start(a, s); e.SetOn(T(s, a));
            e.Until(() => e.On(T(s, a)), "on");
            e.Expect(a.Hook("PreToolUse", s, tool: "AskUserQuestion") == "", "no decision: you answer it yourself");
            e.Hold(() => e.On(T(s, a)) && e.Tile(T(s, a))?.Count == 0, 1000, "not counted as an approval");
        }),

        ("a hook that can't place its window gets a bare tile that works", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var s = Sid();          // never listed (no registry entry for it)
            a.Hook("SessionStart", s, source: "startup");
            e.Until(() => e.Has(s), "bare tile");
            e.Expect(File.Exists(e.Meta(s)), "bare status file");
            e.Click(s);
            e.Until(() => e.On(s), "on");
            e.Expect(e.Tool(a, s), "approves");
            e.KillWidget();
            e.Expect(!e.Tool(a, s), "nothing without the widget");
        }),

        ("an unreadable registry doesn't evict tiles", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var s = Sid();
            e.Start(a, s); e.SetOn(T(s, a));
            e.Until(() => e.On(T(s, a)), "on");
            var f = Path.Combine(e.Registry, a.Pid + ".json");
            var good = File.ReadAllText(f);
            File.WriteAllText(f, "{\"pid\":");             // the only entry, unreadable: nothing to judge by
            e.WaitTicks(25);                               // well past the miss budget
            e.Expect(e.Has(T(s, a)) && e.On(T(s, a)), "tile kept, still on");
            File.WriteAllText(f, good);
            e.Hold(() => e.Has(T(s, a)) && e.On(T(s, a)), 1000, "and after");
            e.Expect(File.Exists(e.Meta(T(s, a))), "its status file kept");
        }),

        ("torn reads of sessions sharing a process don't swap them", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); string s1 = Sid(), s2 = Sid();
            a.Register(s1);
            a.Register(s2, file: a.Pid + "-2.json");
            a.Hook("SessionStart", s1, source: "startup");
            a.Hook("SessionStart", s2, source: "clear");   // s2 came from a /clear: a false swap would switch it on
            e.Until(() => e.Has(T(s1, a)) && e.Has(T(s2, a)), "a tile per session");
            e.SetOn(T(s1, a));
            e.Until(() => e.On(T(s1, a)), "s1 on");
            string f1 = Path.Combine(e.Registry, a.Pid + ".json"), f2 = Path.Combine(e.Registry, a.Pid + "-2.json");
            string good1 = File.ReadAllText(f1), good2 = File.ReadAllText(f2);
            File.WriteAllText(f2, "{\"pid\":");            // the widget reads s1 alone...
            e.WaitTicks(3);
            File.WriteAllText(f1, "{\"pid\":");            // ...then s2 alone
            File.WriteAllText(f2, good2);
            e.WaitTicks(3);
            File.WriteAllText(f1, good1);
            e.Hold(() => e.On(T(s1, a)) && e.Has(T(s2, a)) && !e.On(T(s2, a)), 2000, "s1 keeps autopilot, s2 stays manual");
            e.Expect(e.Tiles()[0].Id == T(s1, a), "s1 kept its slot");
            e.Expect(e.Tool(a, s1) && !e.Tool(a, s2), "s1 approves, s2 prompts");
        }),

        ("Remember: your click in a new window's first seconds stands", () =>
        {
            string s = Sid(), s2 = Sid();
            e.WriteConfig(remember: true, autopilot: new() { [s] = Env.NowMs(), [s2] = Env.NowMs() });
            e.StartWidget();
            var b = e.NewClaude(); var c = e.NewClaude();
            b.Register(s); c.Register(s2);                 // resumed windows, listed before their SessionStart
            e.Until(() => e.On(T(s, b)) && e.On(T(s2, c)), "remembered conversations back on");
            e.Click(T(s, b));                              // you switch b off...
            e.Dismiss(T(s2, c));                           // ...and dismiss c
            e.Until(() => e.Has(T(s, b)) && !e.On(T(s, b)) && !e.Has(T(s2, c)), "b off, c hidden");
            b.Hook("SessionStart", s, source: "resume");   // their SessionStart wipes the switch
            c.Hook("SessionStart", s2, source: "resume");  // and c acting brings its tile back
            e.Until(() => e.Has(T(s2, c)), "c's tile back");
            e.Hold(() => e.Has(T(s, b)) && !e.On(T(s, b)) && e.Has(T(s2, c)) && !e.On(T(s2, c)), 2000, "both stay as you left them");
            e.Expect(!e.Tool(b, s) && !e.Tool(c, s2), "both prompt");
        }),

        ("a hook's write lands while the widget reads the file", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var s = Sid();
            e.Start(a, s); e.SetOn(T(s, a));
            e.Until(() => e.On(T(s, a)), "on");
            // Hold the .meta open the way the widget reads it (ten times a second) while the hook
            // replaces it: until the hook's temp file shows it's waiting, then a moment more.
            var fs = new FileStream(e.Meta(T(s, a)), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var holder = Task.Run(() =>
            {
                using var _ = fs;
                var sw = Stopwatch.StartNew();
                while (Directory.GetFiles(e.Sessions, "*.meta.*.tmp").Length == 0 && sw.ElapsedMilliseconds < 3000) Thread.Sleep(2);
                Thread.Sleep(40);
            });
            e.Expect(e.Tool(a, s), "approved");
            holder.Wait();
            e.Until(() => e.Tile(T(s, a))?.Count == 1, "the approval counted");
        }),

        ("upgrade from bare ids: tile keeps its slot and tally", () =>
        {
            var a = e.NewClaude(); var s = Sid();
            a.Register(s);
            Directory.CreateDirectory(e.Sessions);
            File.WriteAllText(e.Meta(s), "{\"status\":\"waiting\",\"cwd\":\"C:\\\\x\",\"count\":5,\"start\":\"startup\"}");
            e.WriteConfig(order: new() { s });
            e.StartWidget();
            e.Until(() => e.Tile(T(s, a))?.Count == 5 && !e.Has(s), "adopted with its tally");
            e.Until(() => e.ConfigOrder().SequenceEqual(new[] { T(s, a) }), "saved order migrated");
        }),

        ("a new window you switch on before its SessionStart stays on", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var s = Sid();
            a.Register(s);                                 // listed; its SessionStart hasn't run yet
            e.Until(() => e.Has(T(s, a)), "tile");
            e.Click(T(s, a));
            e.Until(() => e.On(T(s, a)), "on");
            a.Hook("SessionStart", s, source: "startup");  // a new session's start wipes the switch
            e.Until(() => File.Exists(e.State(T(s, a))), "switch put back");
            e.Hold(() => e.On(T(s, a)), 1500, "still on");
            e.Expect(e.Tool(a, s), "auto-approves");
        }),

        ("a conversation reopened in a new window gets its old slot back", () =>
        {
            e.StartWidget();
            var a = e.NewClaude(); var b = e.NewClaude(); var c = e.NewClaude(); string s1 = Sid(), s2 = Sid(), s3 = Sid();
            e.Start(a, s1); e.Until(() => e.Has(T(s1, a)), "first tile");
            e.Start(b, s2); e.Until(() => e.Has(T(s2, b)), "second tile");
            e.Start(c, s3); e.Until(() => e.Has(T(s3, c)), "third tile");
            e.Close(b, s2);
            e.Until(() => !e.Has(T(s2, b)), "middle one closed", 5000);
            var b2 = e.NewClaude();
            e.Start(b2, s2, "resume");
            e.Until(() => e.Has(T(s2, b2)), "reopened");
            e.Expect(e.Ids().SequenceEqual(new[] { T(s1, a), T(s2, b2), T(s3, c) }), "back in the middle");
            // Everything closes (a reboot), the widget restarts, and they come back in another order.
            e.Close(a, s1); e.Close(b2, s2); e.Close(c, s3);
            e.Until(() => e.Tiles().Count == 0, "all closed", 5000);
            e.KillWidget();
            e.StartWidget();
            var a2 = e.NewClaude(); var b3 = e.NewClaude(); var c2 = e.NewClaude();
            e.Start(c2, s3, "resume"); e.Until(() => e.Has(T(s3, c2)), "third back");
            e.Start(a2, s1, "resume"); e.Until(() => e.Has(T(s1, a2)), "first back");
            e.Start(b3, s2, "resume"); e.Until(() => e.Has(T(s2, b3)), "second back");
            e.Expect(e.Ids().SequenceEqual(new[] { T(s1, a2), T(s2, b3), T(s3, c2) }), "the old order");
        }),

        ("a stale registry entry whose pid another process now holds gets no tile", () =>
        {
            var a = e.NewClaude(); var x = e.NewClaude(); var s = Sid();
            e.Start(a, s);                                 // a's hook records a's process
            var meta = File.ReadAllText(e.Meta(T(s, a)));
            a.Kill();
            // A reboot later: a's entry and status file are left over, and its pid now belongs to
            // some other process. x stands in for that process (it never lists itself).
            File.Delete(Path.Combine(e.Registry, a.Pid + ".json"));
            File.Delete(e.Meta(T(s, a)));
            x.Register(s);
            File.WriteAllText(e.Meta(T(s, x)), meta);
            e.Age(Path.Combine(e.Registry, x.Pid + ".json"), e.Meta(T(s, x)));
            e.StartWidget();
            e.Until(() => !e.Has(T(s, x)), "no tile for it", 5000);
            e.Hold(() => !e.Has(T(s, x)), 2000, "it stays gone");
            e.KillWidget();                                // its status file is gone now
            e.StartWidget();
            e.Hold(() => !e.Has(T(s, x)), 2500, "still none after a widget restart");
        }),

        ("a window whose status file names an earlier process still gets its tile", () =>
        {
            var old = e.NewClaude(); var a = e.NewClaude(); var s = Sid();
            old.Register(s); old.Hook("Stop", s); old.Unregister();   // a status file naming old's process
            var meta = File.ReadAllText(e.Meta(T(s, old)));
            File.Delete(e.Meta(T(s, old)));
            old.Kill();
            // A crash left a status file under this very tile id: same conversation, and Windows
            // gave the new window's Claude the old one's pid.
            File.WriteAllText(e.Meta(T(s, a)), meta);
            e.Age(e.Meta(T(s, a)));
            e.StartWidget();
            a.Register(s, status: "busy");
            e.WaitTicks(3);                                // judged by the old record: not this process
            a.Hook("SessionStart", s, source: "resume");   // its hook records the real one
            e.Until(() => e.Tile(T(s, a))?.Working == true, "listed again: Claude's own busy status shows");
            e.Hold(() => e.Has(T(s, a)), 2000, "tile stays");
        }),
    };
}

record Tile(string Id, bool On, bool Working, long Count);

sealed class Env
{
    public readonly string Hook, Widget, Work, Home, Registry, Sessions, Config, Payloads;
    Process? _widget;
    readonly List<Fake> _fakes = new();
    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public Env(string hook, string widget, string work)
    {
        Hook = hook; Widget = widget; Work = work;
        Home = Path.Combine(work, "home");
        Registry = Path.Combine(Home, ".claude", "sessions");
        Sessions = Path.Combine(Home, ".claude", "hooker", "sessions");
        Config = Path.Combine(Home, ".claude", "hooker", "widget.json");
        Payloads = Path.Combine(work, "payloads");
        // Inherited by the widget, the fake Claudes and every hook they run.
        Environment.SetEnvironmentVariable("HOOKER_TEST_HOME", Home);
        Environment.SetEnvironmentVariable("HOOKER_TEST_HIDDEN", "1");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", null);   // nothing may point anywhere real
    }

    public void Reset()
    {
        Teardown();
        foreach (var dir in new[] { Home, Payloads })
            for (int i = 0; ; i++)
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); break; }
                catch when (i < 20) { Thread.Sleep(100); }
        Directory.CreateDirectory(Registry);
        Directory.CreateDirectory(Payloads);
    }

    public void Teardown()
    {
        KillWidget();
        foreach (var f in _fakes) f.Kill();
        _fakes.Clear();
    }

    public void StartWidget()
    {
        try { File.Delete(Path.Combine(Home, "tiles.now")); } catch { }
        _widget = Process.Start(new ProcessStartInfo(Widget) { UseShellExecute = false })!;
        _lastTick = -1;
        Until(() => _widget.HasExited || File.Exists(Path.Combine(Home, "tiles.now")), "widget running", 10000);
        // Exiting at once = another test widget holds the test mutex (a leftover run's).
        if (_widget.HasExited) throw new Exception("the test widget exited at once - is another test widget running?");
        Tiles();   // and the tiles are this widget's
    }

    public void KillWidget()
    {
        if (_widget == null) return;
        try { _widget.Kill(); _widget.WaitForExit(5000); } catch { }
        _widget.Dispose(); _widget = null;
    }

    public Fake NewClaude() { var f = new Fake(this); _fakes.Add(f); return f; }

    // A fake Claude that Windows gave a given (freed) pid, or null if it won't reuse it soon.
    public Fake? NewClaudeWithPid(int pid)
    {
        for (int i = 0; i < 100; i++)
        {
            var f = new Fake(this);
            if (f.Pid == pid) { _fakes.Add(f); return f; }
            f.Kill();
        }
        return null;
    }

    // Claude's own order: list the session, then fire SessionStart.
    public void Start(Fake f, string sid, string source = "startup") { f.Register(sid); f.Hook("SessionStart", sid, source: source); }
    // Claude's own order on exit: its registry entry is already gone when SessionEnd fires.
    public void Close(Fake f, string sid) { f.Unregister(); f.Hook("SessionEnd", sid); f.Exit(); }
    // Whether a tool call was auto-approved: no output (Claude asks you) or exactly an allow decision.
    public bool Tool(Fake f, string sid)
    {
        var o = f.Hook("PreToolUse", sid, tool: "Bash");
        if (o.Length == 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(o);
            var h = doc.RootElement.GetProperty("hookSpecificOutput");
            if (h.GetProperty("hookEventName").GetString() == "PreToolUse"
                && h.GetProperty("permissionDecision").GetString() == "allow") return true;
        }
        catch { }
        throw new Exception("hook.exe printed something other than an allow decision: " + o);
    }
    // What the widget writes when you click a tile on (it honours .state on its next tick).
    public void SetOn(string tile) { Directory.CreateDirectory(Sessions); File.WriteAllText(State(tile), "on"); }
    // A real click on the tile / its Dismiss, through the test build's command file.
    public void Click(string tile) => Command("toggle " + tile);
    public void Dismiss(string tile) => Command("dismiss " + tile);
    void Command(string line)
    {
        var path = Path.Combine(Home, "cmd.txt");
        File.WriteAllText(path + ".tmp", line);
        File.Move(path + ".tmp", path, true);
        Until(() => !File.Exists(path), "widget took the click");
    }

    // Files left from before the widget started (a reboot ago).
    public void Age(params string[] files) { foreach (var f in files) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddHours(-1)); }
    public string Meta(string tile) => Path.Combine(Sessions, tile + ".meta");
    public string State(string tile) => Path.Combine(Sessions, tile + ".state");
    public List<string> SessionFiles() =>
        Directory.Exists(Sessions) ? Directory.GetFiles(Sessions).Select(f => Path.GetFileName(f)).ToList() : new();

    public void WriteConfig(bool remember = false, Dictionary<string, long>? autopilot = null, List<string>? order = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Config)!);
        File.WriteAllText(Config, JsonSerializer.Serialize(new
        {
            Order = order ?? new List<string>(), Dismissed = new List<string>(), Meter = false,
            RememberAutopilot = remember, Autopilot = autopilot ?? new Dictionary<string, long>(),
        }));
    }

    public List<string> ConfigOrder()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Config));
            return doc.RootElement.GetProperty("Order").EnumerateArray().Select(x => x.GetString()!).ToList();
        }
        catch { return new(); }
    }

    // The tiles the widget shows, left to right (written by its test probe every tick, under a
    // "#<pid> <tick>" header). Throws unless they come from the widget this scenario started and
    // it's still updating them, so a crashed or frozen widget can't pass a "stays off" check.
    long _lastTick = -1;
    readonly Stopwatch _sinceTick = Stopwatch.StartNew();
    public List<Tile> Tiles()
    {
        string[]? lines = null;
        for (int i = 0; i < 20 && lines == null; i++)
            try { lines = File.ReadAllLines(Path.Combine(Home, "tiles.now")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(20); }
        if (lines == null || lines.Length == 0 || !lines[0].StartsWith('#')) throw new Exception("can't read the test widget's tiles");
        var h = lines[0][1..].Split(' ');
        if (_widget == null || int.Parse(h[0]) != _widget.Id) throw new Exception($"tiles come from another widget (pid {h[0]})");
        long tick = long.Parse(h[1]);
        if (tick != _lastTick) { _lastTick = tick; _sinceTick.Restart(); }
        else if (_sinceTick.ElapsedMilliseconds > 2000) throw new Exception("the test widget stopped updating its tiles");
        var tiles = new List<Tile>();
        foreach (var line in lines.Skip(1))
        {
            var p = line.Split(' ');
            if (p.Length == 4) tiles.Add(new Tile(p[0], p[1] == "on", p[2] == "working", long.Parse(p[3])));
        }
        return tiles;
    }
    public Tile? Tile(string id) => Tiles().FirstOrDefault(t => t.Id == id);
    public List<string> Ids() => Tiles().Select(t => t.Id).ToList();
    public bool Has(string id) => Tile(id) != null;
    public bool On(string id) => Tile(id)?.On == true;
    public string Describe()
    {
        try { var t = Tiles(); return t.Count == 0 ? "(none)" : string.Join("  ", t.Select(x => $"{x.Id}[{(x.On ? "on" : "off")},{(x.Working ? "working" : "waiting")},{x.Count}]")); }
        catch (Exception ex) { return "(" + ex.Message + ")"; }
    }

    // Wait until the widget has synced n more times (e.g. so it has surely read a torn file).
    public void WaitTicks(int n)
    {
        Tiles();
        long from = _lastTick;
        Until(() => { Tiles(); return _lastTick >= from + n; }, $"{n} widget ticks");
    }

    public void Expect(bool ok, string what) { if (!ok) throw new Exception("expected: " + what); }

    public void Until(Func<bool> cond, string what, int timeoutMs = 4000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new Exception($"timed out ({timeoutMs} ms) waiting for: {what}");
            Thread.Sleep(50);
        }
    }

    public void Hold(Func<bool> cond, int ms, string what)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (!cond()) throw new Exception($"stopped holding after {sw.ElapsedMilliseconds} ms: {what}");
            Thread.Sleep(50);
        }
    }
}

// A stand-in Claude process: listed in the test registry under its own pid; runs hooks as its
// descendants (fake-claude mode, below), so hook.exe's parent walk finds it as Claude's would.
sealed class Fake
{
    readonly Env _env;
    readonly Process _p;
    public readonly int Pid;
    readonly List<string> _files = new();

    public Fake(Env env)
    {
        _env = env;
        _p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "fake-claude")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true,
        })!;
        Pid = _p.Id;
    }

    // status null = the field is absent; "" = present but empty (claude -r's picker placeholder).
    public void Register(string sid, string? status = "idle", string? procStart = null, string? file = null)
    {
        var entry = new Dictionary<string, object> { ["pid"] = Pid, ["sessionId"] = sid, ["cwd"] = "C:\\x", ["kind"] = "interactive" };
        entry["startedAt"] = Env.NowMs();
        entry["procStart"] = procStart ?? _p.StartTime.ToFileTimeUtc().ToString();
        if (status != null) entry["status"] = status;
        var name = file ?? Pid + ".json";
        var path = Path.Combine(_env.Registry, name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(entry));
        File.Move(path + ".tmp", path, true);
        if (!_files.Contains(name)) _files.Add(name);
    }

    public void Unregister()
    {
        foreach (var f in _files) try { File.Delete(Path.Combine(_env.Registry, f)); } catch { }
        _files.Clear();
    }

    string Payload(string evt, string sid, string? source, string? tool)
    {
        var d = new Dictionary<string, string> { ["hook_event_name"] = evt, ["session_id"] = sid, ["cwd"] = "C:\\x" };
        if (source != null) d["source"] = source;
        if (tool != null) d["tool_name"] = tool;
        var path = Path.Combine(_env.Payloads, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(d));
        return path;
    }

    string Send(string line)
    {
        _p.StandardInput.WriteLine(line);
        _p.StandardInput.Flush();
        var reply = _p.StandardOutput.ReadLine() ?? "err fake Claude exited";
        if (!reply.StartsWith("ok")) throw new Exception("fake Claude: " + reply);
        return reply;
    }

    // Runs hook.exe for one event and returns what it printed on stdout. It must exit 0, and print
    // nothing for anything but PreToolUse (Claude would take that output as context or an error).
    public string Hook(string evt, string sid, string? source = null, string? tool = null)
    {
        var payload = Payload(evt, sid, source, tool);
        var outFile = payload + ".out";
        ExpectExit0(Send($"hook\t{_env.Hook}\t{payload}\t{outFile}"), evt);
        var o = File.ReadAllText(outFile);
        if (evt != "PreToolUse" && o.Length > 0) throw new Exception($"hook.exe printed on {evt}: {o}");
        return o;
    }

    static void ExpectExit0(string reply, string what)
    {
        if (reply != "ok 0") throw new Exception($"hook.exe ({what}) exited with {reply[2..].Trim()}");
    }

    // Same, but while the hook runs, the registry also holds a dead Claude's entry under the
    // pid of the shell running it (Windows reused that pid).
    public string HookStale(string evt, string sid, string staleSid)
    {
        var payload = Payload(evt, sid, null, "Bash");
        var outFile = payload + ".out";
        ExpectExit0(Send($"hook-stale\t{_env.Hook}\t{payload}\t{outFile}\t{_env.Registry}\t{staleSid}"), evt);
        return File.ReadAllText(outFile);
    }

    public void HookParallel(int n, string evt, string sid)
    {
        var payload = Payload(evt, sid, null, "Bash");
        Send($"hook-par\t{_env.Hook}\t{payload}\t{payload}.out\t{n}");
    }

    public void Exit()
    {
        try { _p.StandardInput.WriteLine("exit"); _p.StandardInput.Flush(); _p.WaitForExit(3000); } catch { }
    }

    public void Kill()
    {
        try { if (!_p.HasExited) { _p.Kill(); _p.WaitForExit(3000); } } catch { }
    }
}

static class FakeClaudeMode
{
    public static int Run()
    {
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            var p = line.Split('\t');
            try
            {
                switch (p[0])
                {
                    case "hook": Console.WriteLine(Shell(p[1], p[2], p[3], null, null)); break;
                    case "hook-stale": Console.WriteLine(Shell(p[1], p[2], p[3], p[4], p[5])); break;
                    case "hook-par":
                        var runs = Enumerable.Range(0, int.Parse(p[4]))
                            .Select(i => Task.Run(() => Shell(p[1], p[2], p[3] + "." + i, null, null))).ToArray();
                        Task.WaitAll(runs);
                        var bad = runs.Select(r => r.Result).FirstOrDefault(r => r != "ok 0");
                        Console.WriteLine(bad == null ? "ok" : "err a parallel hook: " + bad);
                        break;
                    case "exit": return 0;
                    default: Console.WriteLine("err unknown command " + p[0]); break;
                }
            }
            catch (Exception ex) { Console.WriteLine("err " + ex.Message.Replace('\n', ' ')); }
            Console.Out.Flush();
        }
        return 0;
    }

    // cmd.exe stands in for the shell Claude runs hooks through.
    static string Shell(string hook, string payload, string outFile, string? registry, string? staleSid)
    {
        var cmd = $"\"{hook}\" < \"{payload}\" > \"{outFile}\"";
        if (staleSid != null) cmd = "ping -n 2 127.0.0.1 >nul & " + cmd;   // time to plant the stale entry first
        using var pr = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /s /c \"{cmd}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        string? stale = null;
        if (staleSid != null)
        {
            stale = Path.Combine(registry!, pr.Id + ".json");
            File.WriteAllText(stale, JsonSerializer.Serialize(new { pid = pr.Id, sessionId = staleSid, status = "idle" }));
        }
        // A hung hook would hang Claude: fail the scenario instead of the whole run.
        bool done = pr.WaitForExit(15000);
        if (!done) try { pr.Kill(true); } catch { }
        if (stale != null) try { File.Delete(stale); } catch { }
        return done ? "ok " + pr.ExitCode : "err hook.exe still running after 15 s";
    }
}
