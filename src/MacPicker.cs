using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace StickerStudio;

/// The real macOS file picker when the game runs in Wine on a Mac (Vinora). Wine can start Mac programs, so this
/// writes a small JavaScript-for-Automation script to /tmp and runs /usr/bin/osascript on it. The script opens an
/// NSOpenPanel and keeps it at window level 100 — above the game's window (a helper can't steal focus on recent
/// macOS, so "activate" alone left it behind), but under level 101 where the panel's own menus open. The answer is
/// written to a result file, which this thread waits for. Mac paths are Z:\... inside Wine.
internal static class MacPicker
{
    const string Osascript = @"Z:\usr\bin\osascript";

    const string Script = @"
ObjC.import('AppKit');
function answer(text) { $(text).writeToFileAtomicallyEncodingError(OUT + '.txt', true, $.NSUTF8StringEncoding, null); }
$('').writeToFileAtomicallyEncodingError(OUT + '.started', true, $.NSUTF8StringEncoding, null);
try {
  var app = $.NSApplication.sharedApplication;
  app.setActivationPolicy($.NSApplicationActivationPolicyAccessory);
  app.activateIgnoringOtherApps(true);
  var panel = $.NSOpenPanel.openPanel;
  panel.title = 'Pick a picture for your sticker';
  panel.message = 'Pick a picture for your sticker';
  panel.prompt = 'Use picture';
  panel.canChooseDirectories = false;
  panel.allowsMultipleSelection = false;
  panel.allowedFileTypes = $(['png', 'jpg', 'jpeg']);
  if (START) panel.directoryURL = $.NSURL.fileURLWithPath(START);
  var raised = false;
  var timer = $.NSTimer.timerWithTimeIntervalRepeatsBlock(0.15, true, function () {
    if (panel.level != 100) panel.level = 100;
    if (!raised && panel.isVisible) { raised = true; panel.orderFrontRegardless; panel.makeKeyWindow; }
  });
  $.NSRunLoop.currentRunLoop.addTimerForMode(timer, $.NSRunLoopCommonModes);
  var result = panel.runModal;
  timer.invalidate;
  if (result == $.NSModalResponseOK) answer('OK:' + ObjC.unwrap(panel.URLs.objectAtIndex(0).path));
  else answer('ERR:-128:cancelled');
} catch (e) {
  answer('ERR:-1:' + e);
}
";

    public static bool CanUse
    {
        get
        {
            try { return Plugin.MacFilePicker.Value && File.Exists(Osascript) && Directory.Exists(@"Z:\tmp"); }
            catch { return false; }
        }
    }

    /// The picked file as a Windows (Wine) path, or null if cancelled. Throws if the picker couldn't run.
    public static string Pick(string startFolder)
    {
        var id = Guid.NewGuid().ToString("N").Substring(0, 12);
        var unix = $"/tmp/fabuleo-sticker-{id}";
        var win = $@"Z:\tmp\fabuleo-sticker-{id}";
        var start = startFolder != null && Directory.Exists(startFolder) ? ToUnix(startFolder) : null;
        var header = $"var OUT = {JsonSerializer.Serialize(unix)};\nvar START = {JsonSerializer.Serialize(start ?? DefaultFolder())};\n";
        File.WriteAllText(win + ".js", header + Script, new UTF8Encoding(false));
        try
        {
            using (Process.Start(new ProcessStartInfo(Osascript, $"-l JavaScript {unix}.js") { UseShellExecute = false, CreateNoWindow = true })) { }

            // The script says it started; if it never does, Wine couldn't run it: let the Windows picker take over.
            var waited = 0;
            while (!File.Exists(win + ".started"))
            {
                if ((waited += 100) > 8000) throw new TimeoutException("osascript didn't start");
                Thread.Sleep(100);
            }
            while (!File.Exists(win + ".txt")) Thread.Sleep(150); // the picker is open as long as the player needs

            var result = File.ReadAllText(win + ".txt", Encoding.UTF8);
            if (result.StartsWith("OK:")) return ToWindows(result.Substring(3).Trim());
            if (result.StartsWith("ERR:-128")) return null; // cancelled
            throw new InvalidOperationException(result);
        }
        finally
        {
            foreach (var ext in new[] { ".js", ".started", ".txt" })
                try { File.Delete(win + ext); } catch { /* best effort */ }
        }
    }

    /// The Mac Desktop (Z:\Users\<you>\Desktop), as a Mac path.
    static string DefaultFolder()
    {
        try
        {
            foreach (var user in Directory.GetDirectories(@"Z:\Users"))
            {
                var desktop = Path.Combine(user, "Desktop");
                if (!user.EndsWith("Shared", StringComparison.OrdinalIgnoreCase) && Directory.Exists(desktop)) return ToUnix(desktop);
            }
        }
        catch { /* no Mac disk */ }
        return null;
    }

    /// Z:\Users\me\Desktop -> /Users/me/Desktop (only Mac-disk paths; others have no Mac equivalent here).
    static string ToUnix(string windowsPath) =>
        windowsPath.StartsWith(@"Z:\", StringComparison.OrdinalIgnoreCase) ? "/" + windowsPath.Substring(3).Replace('\\', '/') : null;

    static string ToWindows(string unixPath) => "Z:" + unixPath.Replace('/', '\\');
}
