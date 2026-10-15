using System.Reflection;
using Tiny11Builder.Core;
var core = typeof(ProcessRunner).Assembly;
var ru = core.GetType("<RuPatch>");
Console.WriteLine("RuPatch type: " + (ru?.FullName ?? "NULL"));
try
{
    var m = ru!.GetMethod("OemEncoding", BindingFlags.Public | BindingFlags.Static);
    var enc = m!.Invoke(null, null);
    Console.WriteLine("OemEncoding OK -> " + (enc?.ToString() ?? "null (fallback)"));
}
catch (Exception e) { Console.WriteLine("OemEncoding FAIL: " + e.InnerException?.GetType().Name + ": " + e.InnerException?.Message); }
try
{
    var runner = new ProcessRunner();
    var res = await runner.RunAsync("/bin/echo", new[] { "privet-mir" }, line => Console.WriteLine("  out: " + line), CancellationToken.None);
    Console.WriteLine($"RUN OK exit={res.ExitCode} output=[{string.Join("|", res.Output)}]");
}
catch (Exception e)
{
    Console.WriteLine("RunAsync FAIL: " + e.GetType().Name + ": " + e.Message);
    Console.WriteLine(e.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains("Tiny11Builder"))?.Trim());
    Environment.Exit(2);
}
