using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace AdVoid.Tests;

// The page scripts, run for real in Node against a pretend page (Scriptlets/*.test.mjs). Where Node isn't installed
// the test says so and passes; the nightly check runs on a PC that has it.
public class ScriptletTests(ITestOutputHelper output)
{
    [Fact]
    public void YouTubeScriptRemovesAdsAndThePopupWithoutShowingItself()
    {
        var node = FindNode();
        if (node is null) { output.WriteLine("Node isn't installed: youtube.js not run"); return; }
        var script = Path.Combine(Path.GetTempPath(), $"advoid-youtube-{Guid.NewGuid():N}.js");
        File.WriteAllText(script, Scriptlets.YouTube);
        try
        {
            var test = Path.Combine(AppContext.BaseDirectory, "Scriptlets", "youtube.test.mjs");
            using var process = Process.Start(new ProcessStartInfo(node, [test, script]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            output.WriteLine(text);
            Assert.True(process.ExitCode == 0, text);
        }
        finally { File.Delete(script); }
    }

    private static string? FindNode() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir.Trim('"'), OperatingSystem.IsWindows() ? "node.exe" : "node"))
            .FirstOrDefault(File.Exists);
}
