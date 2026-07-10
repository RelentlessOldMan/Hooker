// Hooker shim — one exe wired to several Claude Code hook events, now PER SESSION.
// Claude runs it once per event and reads its stdout. Two jobs:
//
//   1. PreToolUse: if THIS session is "hooking" (its .state file says on), print an
//      allow decision so its prompts auto-approve. Otherwise print nothing / exit 0,
//      leaving Claude's normal behaviour untouched.
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

    string evt = "", tool = "", sid = "", cwd = "";
    try
    {
        using var doc = JsonDocument.Parse(stdin);
        var root = doc.RootElement;
        if (root.TryGetProperty("hook_event_name", out var e)) evt = e.GetString() ?? "";
        if (root.TryGetProperty("tool_name", out var t)) tool = t.GetString() ?? "";
        if (root.TryGetProperty("session_id", out var s)) sid = s.GetString() ?? "";
        if (root.TryGetProperty("cwd", out var c)) cwd = c.GetString() ?? "";
    }
    catch { /* no/invalid payload */ }

    if (sid.Length == 0) return 0; // nothing session-scoped to do
    sid = Sanitize(sid);

    var metaPath = Path.Combine(sessionsDir, sid + ".meta");
    var statePath = Path.Combine(sessionsDir, sid + ".state");

    Meta ReadMeta()
    {
        try
        {
            if (File.Exists(metaPath))
                return JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaPath)) ?? new Meta();
        }
        catch { }
        return new Meta();
    }
    void WriteMeta(Meta m)
    {
        try
        {
            Directory.CreateDirectory(sessionsDir);
            // Write-then-rename so the widget never reads a half-written .meta.
            var tmp = metaPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(m));
            File.Move(tmp, metaPath, overwrite: true);
        }
        catch { }
    }
    void SetStatus(string status, bool bump = false)
    {
        var m = ReadMeta();
        m.status = status;
        if (cwd.Length > 0) m.cwd = cwd;
        if (bump) m.count += 1;
        WriteMeta(m);
    }
    bool Hooking()
    {
        try
        {
            return File.Exists(statePath) &&
                   File.ReadAllText(statePath).Trim().Equals("on", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    switch (evt)
    {
        case "SessionStart":
            // New (or resumed) session: it's awaiting your first prompt; reset the tally.
            var start = ReadMeta();
            start.status = "waiting";
            if (cwd.Length > 0) start.cwd = cwd;
            start.count = 0;
            WriteMeta(start);
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
    // Fail open to normal behaviour — never disrupt Claude.
}

return 0;

sealed class Meta
{
    public string status { get; set; } = "working";
    public string cwd { get; set; } = "";
    public long count { get; set; } = 0;
}
