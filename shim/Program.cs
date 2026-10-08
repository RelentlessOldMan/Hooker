// Hooker shim — one exe wired to several Claude Code hook events, now PER SESSION.
// Claude runs it once per event and reads its stdout. Two jobs:
//
//   1. PreToolUse: if THIS session is "hooking" (its .state file says on) AND the widget is
//      running, print an allow decision so its prompts auto-approve. Otherwise print nothing /
//      exit 0, leaving Claude's normal behaviour untouched.
//
//   2. Per-session status: translate lifecycle events into each session's .meta file
//      (status working/waiting, cwd, auto-approval count) that the widget renders.
//
// State lives under %USERPROFILE%\.claude\hooker\sessions\:
//   <sid>.meta   {"status","cwd","count"}   (this shim writes; widget reads)
//   <sid>.state  "on"/"off"                 (widget writes; this shim reads)
//
// Design rule: NEVER break Claude. Any error => print nothing, exit 0.

using System.Text;
using System.Text.Json;

var sessionsDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".claude", "hooker", "sessions");

static string AllowJson() => JsonSerializer.Serialize(new
{
    hookSpecificOutput = new
    {
        hookEventName = "PreToolUse",
        permissionDecision = "allow",
        permissionDecisionReason = "Auto-approved by Hooker mascot (hooking mode)",
    },
});

static string Sanitize(string s)
{
    var sb = new StringBuilder(s.Length);
    foreach (var c in s)
        sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
    return sb.Length == 0 ? "_" : sb.ToString();
}

try
{
    var stdin = Console.In.ReadToEnd();

    string evt = "", tool = "", sid = "", cwd = "", source = "";
    try
    {
        using var doc = JsonDocument.Parse(stdin);
        var root = doc.RootElement;
        if (root.TryGetProperty("hook_event_name", out var e)) evt = e.GetString() ?? "";
        if (root.TryGetProperty("tool_name", out var t)) tool = t.GetString() ?? "";
        if (root.TryGetProperty("session_id", out var s)) sid = s.GetString() ?? "";
        if (root.TryGetProperty("cwd", out var c)) cwd = c.GetString() ?? "";
        if (root.TryGetProperty("source", out var so)) source = so.GetString() ?? "";   // SessionStart: startup|resume|clear|compact
    }
    catch { /* no/invalid payload */ }

    if (sid.Length == 0) return 0; // nothing session-scoped to do
    sid = Sanitize(sid);

    var metaPath = Path.Combine(sessionsDir, sid + ".meta");
    var statePath = Path.Combine(sessionsDir, sid + ".state");

    // Read allowing the widget (or another shim) to hold the file open — a plain File.ReadAllText
    // denies writers, so a concurrent rename/write would throw and we'd lose the read.
    static string? ReadShared(string path)
    {
        try
        {
            // FileShare.Delete too, so a concurrent rename-over (File.Move overwrite) of this file
            // by the widget or another shim isn't blocked by our open handle.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch { return null; }
    }
    Meta ReadMeta()
    {
        try
        {
            var t = ReadShared(metaPath);
            if (t != null) return JsonSerializer.Deserialize<Meta>(t) ?? new Meta();
        }
        catch { }
        return new Meta();
    }
    void WriteMeta(Meta m)
    {
        try
        {
            Directory.CreateDirectory(sessionsDir);
            // Write-then-rename so the widget never reads a half-written .meta. The temp name is
            // per-process so two shim invocations for the SAME session (Claude can fire tools in
            // parallel) can't write the same tmp and corrupt it; the finally clears a lost tmp.
            var tmp = metaPath + "." + Environment.ProcessId + ".tmp";
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(m));
                File.Move(tmp, metaPath, overwrite: true);
            }
            finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        }
        catch { }
    }
    // Claude fires tool hooks in parallel, so several shims can read-modify-write the same .meta
    // at once and lose an auto-approval count. Serialise per session with a named mutex; if it
    // can't be had quickly, carry on unlocked rather than ever stall Claude.
    void UpdateMeta(Action<Meta> change)
    {
        Mutex? mx = null;
        bool held = false;
        try
        {
            mx = new Mutex(false, "Hooker.Meta." + sid);
            try { held = mx.WaitOne(500); } catch (AbandonedMutexException) { held = true; }
        }
        catch { }
        try
        {
            var m = ReadMeta();
            change(m);
            WriteMeta(m);
        }
        finally
        {
            if (held) try { mx!.ReleaseMutex(); } catch { }
            mx?.Dispose();
        }
    }
    void SetStatus(string status, bool bump = false) => UpdateMeta(m =>
    {
        m.status = status;
        if (cwd.Length > 0) m.cwd = cwd;
        if (bump) m.count += 1;
    });
    // Auto-approve needs BOTH this session's .state = "on" AND a running widget (it holds this
    // named mutex for its whole life). The widget turns sessions off when it exits cleanly, but a
    // crash or kill skips that - so a widget that isn't running grants nothing, whatever the
    // files on disk say. Any failure here also means "not hooking": fail safe, to normal prompts.
    bool Hooking()
    {
        var t = ReadShared(statePath);
        if (t == null || !t.Trim().Equals("on", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            if (!Mutex.TryOpenExisting("Hooker.Widget.SingleInstance", out var widget)) return false;
            widget.Dispose();
            return true;
        }
        catch { return false; }
    }

    switch (evt)
    {
        case "SessionStart":
            // Awaiting your first prompt. A NEW or RESUMED session starts manual with a fresh tally:
            // a resumed session keeps its old id, and must not come back already on autopilot from a
            // .state its previous run left behind. Auto-compact keeps the id, so it keeps both.
            // /clear gets a NEW id (its old one ends) - the widget sees the same process swap ids and
            // carries autopilot over once it reads start == "clear" here.
            bool fresh = source is not ("clear" or "compact");
            if (fresh) { try { File.Delete(statePath); } catch { } }
            UpdateMeta(m =>
            {
                m.status = "waiting";
                m.start = source;
                if (cwd.Length > 0) m.cwd = cwd;
                if (fresh) m.count = 0;
            });
            break;

        case "UserPromptSubmit":
            SetStatus("working");
            break;

        case "PreToolUse":
            if (tool == "AskUserQuestion") { SetStatus("waiting"); break; } // needs YOU to pick
            if (Hooking())
            {
                Console.Out.Write(AllowJson());
                SetStatus("working", bump: true);
            }
            else SetStatus("working");
            break;

        case "Notification": // needs permission / attention
        case "Stop":         // finished its turn, awaiting your next instruction
            SetStatus("waiting");
            break;

        case "SessionEnd":
            try { File.Delete(metaPath); } catch { }
            try { File.Delete(statePath); } catch { }
            break;
    }
}
catch
{
    // Fail safe to normal behaviour (no decision = Claude prompts as usual) — never disrupt Claude.
}

return 0;

// Lowercase property names mirror the on-disk .meta JSON keys the widget reads.
sealed class Meta
{
    public string status { get; set; } = "working";
    public string cwd { get; set; } = "";
    public long count { get; set; } = 0;
    public string start { get; set; } = "";   // SessionStart source: startup|resume|clear|compact
}
