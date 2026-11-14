using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using SpankyhoOptimizer.Services;

if (args.Length > 0 && args[0] == "child")
{
    File.WriteAllText(args[1], Environment.ProcessId.ToString());
    if (args.Length > 2)
    {
        var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"child \"{args[2]}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        child.Dispose();
    }
    await Task.Delay(TimeSpan.FromSeconds(60));
    return 0;
}
var failed = 0;
await Check("shell cancellation kills process and descendant", ShellCancellation);
await Check("pre-cancelled shell does not launch", ShellPreCancelled);
await Check("shell timeout kills process and descendant", ShellTimeout);
await Check("unelevated cancellation kills descendant", UnelevatedCancellation);
await Check("streaming invalid timeout does not launch", InvalidStreamingTimeout);
await Check("stream drain bounded when descendant inherits pipes", InheritedPipeDrain);
await Check("unelevated partial log line preserved", PartialLogLine);
await Check("streaming cancellation kills process and descendant", StreamingCancellation);
await Check("streaming timeout kills process and descendant", StreamingTimeout);
await Check("unelevated timeout kills descendant", UnelevatedTimeout);
await Check("unelevated final unterminated line flushed", FinalPartialLogLine);
await Check("unelevated split CRLF creates no extra line", SplitCrLf);
await Check("streaming captures output, errors and exit status", StreamingReadback);
await Check("modernizer cancellation kills descendant", ModernizerCancellation);
await Check("modernizer preserves exit and ignore semantics", ModernizerExit);
return failed == 0 ? 0 : 1;

async Task Check(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
async Task ShellCancellation()
{
    using var fixture = new Children();
    using var cts = new CancellationTokenSource();
    var task = ProcessRunner.StartShellAndWaitAsync(Environment.ProcessPath!, fixture.Arguments, cancellationToken: cts.Token);
    await fixture.Ready(); cts.Cancel();
    try { await task.WaitAsync(TimeSpan.FromSeconds(8)); throw new Exception("Cancellation not propagated"); } catch (OperationCanceledException) {}
    fixture.AssertStopped();
}
async Task ShellTimeout()
{
    using var fixture = new Children();
    var method = typeof(ProcessRunner).GetMethod("StartShellAndWaitAsync")!;
    if (method.GetParameters().Length < 5) throw new Exception("Shell wait has no bounded timeout");
    var task = (Task<ProcessResult>)method.Invoke(null, new object?[] {Environment.ProcessPath!, fixture.Arguments, null, CancellationToken.None, TimeSpan.FromSeconds(2)})!;
    await fixture.Ready();
    try { await task.WaitAsync(TimeSpan.FromSeconds(8)); throw new Exception("Timeout not propagated"); } catch (TimeoutException) {}
    fixture.AssertStopped();
}
async Task ShellPreCancelled()
{
    using var fixture = new Children();
    using var cts = new CancellationTokenSource(); cts.Cancel();
    try { await ProcessRunner.StartShellAndWaitAsync(Environment.ProcessPath!, fixture.Arguments, cancellationToken: cts.Token); } catch (OperationCanceledException) {}
    await Task.Delay(1500);
    if (File.Exists(fixture.Parent)) throw new Exception("Already cancelled call launched process");
}
async Task UnelevatedCancellation()
{
    using var fixture = new Children();
    using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, fixture.Arguments) { UseShellExecute = false, CreateNoWindow = true })!;
    await fixture.Ready();
    var type = typeof(UnelevatedProcessRunner);
    var piType = type.GetNestedType("PROCESS_INFORMATION", BindingFlags.NonPublic)!;
    var pi = Activator.CreateInstance(piType)!;
    piType.GetField("hProcess")!.SetValue(pi, Native.OpenProcess(0x00100001, false, (uint)process.Id));
    piType.GetField("dwProcessId")!.SetValue(pi, (uint)process.Id);
    using var cts = new CancellationTokenSource();
    var task = (Task<ProcessResult>)type.GetMethod("WaitForProcessAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object?[] {pi, fixture.Parent + ".log", TimeSpan.FromSeconds(10), cts.Token, null})!;
    cts.Cancel();
    try { await task.WaitAsync(TimeSpan.FromSeconds(8)); throw new Exception("Cancellation not propagated"); } catch (OperationCanceledException) {}
    fixture.AssertStopped();
}
async Task InvalidStreamingTimeout()
{
    using var fixture = new Children();
    try { await ProcessRunner.RunAsync(Environment.ProcessPath!, fixture.Arguments, TimeSpan.FromMilliseconds(-2)); throw new Exception("Invalid timeout accepted"); } catch (ArgumentOutOfRangeException) {}
    await Task.Delay(1500);
    if (File.Exists(fixture.Parent)) throw new Exception("Invalid timeout launched abandoned process");
}
async Task InheritedPipeDrain()
{
    using var fixture = new Children();
    var task = ProcessRunner.RunAsync("cmd.exe", $"/c start \"\" /b \"{Environment.ProcessPath}\" {fixture.Arguments} & exit /b 0", TimeSpan.FromSeconds(10));
    try
    {
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!result.Success) throw new Exception("Parent exit result lost");
    }
    finally { fixture.Dispose(); try { await task; } catch {} }
}
Task PartialLogLine()
{
    string log = Path.Combine(Path.GetTempPath(), "spanky-log-test-" + Guid.NewGuid());
    var output = new System.Text.StringBuilder();
    var method = typeof(UnelevatedProcessRunner).GetMethod("ReadNewLines", BindingFlags.NonPublic | BindingFlags.Static)!;
    object?[] values = method.GetParameters().Length == 4 ? new object?[]{log, 0, output, null} : new object?[]{log, 0, output, null, false};
    try
    {
        File.WriteAllText(log, "part"); method.Invoke(null, values);
        File.AppendAllText(log, "ial\r\n"); method.Invoke(null, values);
        if (output.ToString() != "partial" + Environment.NewLine) throw new Exception($"Partial line was lost: {output.ToString().Trim()}");
    }
    finally { File.Delete(log); }
    return Task.CompletedTask;
}
async Task StreamingCancellation()
{
    using var fixture = new Children(); using var cts = new CancellationTokenSource();
    var task = ProcessRunner.RunAsync(Environment.ProcessPath!, fixture.Arguments, TimeSpan.FromSeconds(10), cts.Token);
    await fixture.Ready(); cts.Cancel();
    try { await task.WaitAsync(TimeSpan.FromSeconds(8)); throw new Exception("Cancellation not propagated"); } catch (OperationCanceledException) {}
    fixture.AssertStopped();
}
async Task StreamingTimeout()
{
    using var fixture = new Children();
    var task = ProcessRunner.RunAsync(Environment.ProcessPath!, fixture.Arguments, TimeSpan.FromSeconds(2));
    await fixture.Ready();
    try { await task.WaitAsync(TimeSpan.FromSeconds(8)); throw new Exception("Timeout not propagated"); } catch (TimeoutException) {}
    fixture.AssertStopped();
}
async Task UnelevatedTimeout()
{
    using var fixture = new Children();
    using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, fixture.Arguments) { UseShellExecute = false, CreateNoWindow = true })!;
    await fixture.Ready();
    var type = typeof(UnelevatedProcessRunner); var piType = type.GetNestedType("PROCESS_INFORMATION", BindingFlags.NonPublic)!; var pi = Activator.CreateInstance(piType)!;
    piType.GetField("hProcess")!.SetValue(pi, Native.OpenProcess(0x00100001, false, (uint)process.Id)); piType.GetField("dwProcessId")!.SetValue(pi, (uint)process.Id);
    var task = (Task<ProcessResult>)type.GetMethod("WaitForProcessAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object?[] {pi, fixture.Parent + ".log", TimeSpan.Zero, CancellationToken.None, null})!;
    try { await task.WaitAsync(TimeSpan.FromSeconds(8)); throw new Exception("Timeout not propagated"); } catch (TimeoutException) {}
    fixture.AssertStopped();
}
Task FinalPartialLogLine()
{
    string log = Path.Combine(Path.GetTempPath(), "spanky-log-test-" + Guid.NewGuid()); var output = new System.Text.StringBuilder();
    var method = typeof(UnelevatedProcessRunner).GetMethod("ReadNewLines", BindingFlags.NonPublic | BindingFlags.Static)!;
    try { File.WriteAllText(log,"tail"); method.Invoke(null,new object?[]{log,0,output,null,true}); if(output.ToString() != "tail" + Environment.NewLine) throw new Exception("Final line lost"); }
    finally { File.Delete(log); }
    return Task.CompletedTask;
}
Task SplitCrLf()
{
    string log = Path.Combine(Path.GetTempPath(), "spanky-log-test-" + Guid.NewGuid()); var output = new System.Text.StringBuilder();
    var method = typeof(UnelevatedProcessRunner).GetMethod("ReadNewLines", BindingFlags.NonPublic | BindingFlags.Static)!; object?[] values = {log,0,output,null,false};
    try { File.WriteAllText(log,"line\r"); method.Invoke(null,values); File.AppendAllText(log,"\n"); method.Invoke(null,values); if(output.ToString() != "line" + Environment.NewLine) throw new Exception("Split CRLF added a spurious line"); }
    finally { File.Delete(log); }
    return Task.CompletedTask;
}
async Task ModernizerCancellation()
{
    using var fixture = new Children(); using var cts = new CancellationTokenSource();
    var method = typeof(WindowsModernizerService).GetMethod("RunAsync",BindingFlags.NonPublic|BindingFlags.Static)!;
    var task = (Task)method.Invoke(null,new object?[]{Environment.ProcessPath!,fixture.Arguments,cts.Token,false})!;
    await fixture.Ready(); cts.Cancel();
    try { await task.WaitAsync(TimeSpan.FromSeconds(8)); throw new Exception("Cancellation not propagated"); } catch(OperationCanceledException) {}
    fixture.AssertStopped();
}
async Task ModernizerExit()
{
    var method=typeof(WindowsModernizerService).GetMethod("RunAsync",BindingFlags.NonPublic|BindingFlags.Static)!;
    var strict=(Task)method.Invoke(null,new object?[]{"cmd.exe","/c echo fixture-error 1>&2 & exit /b 7",CancellationToken.None,false})!;
    try { await strict; throw new Exception("Nonzero exit ignored"); }catch(InvalidOperationException ex) {if(!ex.Message.Contains("fixture-error"))throw;}
    var permissive=(Task)method.Invoke(null,new object?[]{"cmd.exe","/c echo fixture-error 1>&2 & exit /b 7",CancellationToken.None,true})!;
    await permissive;
    object result=permissive.GetType().GetProperty("Result")!.GetValue(permissive)!;
    if((int)result.GetType().GetProperty("ExitCode")!.GetValue(result)! != 7) throw new Exception("ExitCode lost");
}
async Task StreamingReadback()
{
    var result = await ProcessRunner.RunAsync("cmd.exe", "/c echo OUTPUT & echo ERROR 1>&2 & exit /b 7", TimeSpan.FromSeconds(5), logNonZeroExitAsError:false);
    if (result.ExitCode != 7 || result.Success || !result.StdOut.Contains("OUTPUT") || !result.StdErr.Contains("ERROR")) throw new Exception("Output or exit status lost");
}
sealed class Children : IDisposable
{
    public string Parent = Path.Combine(Path.GetTempPath(), "spanky-process-test-" + Guid.NewGuid() + ".pid");
    public string Child => Parent + ".child";
    public string Arguments => $"child \"{Parent}\" \"{Child}\"";
    public async Task Ready() { var until = Stopwatch.StartNew(); while ((!File.Exists(Parent) || !File.Exists(Child)) && until.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(25); if (!File.Exists(Child)) throw new Exception("Child fixture failed to start"); }
    public void AssertStopped() { foreach(var path in new[]{Parent,Child}) if (Alive(path)) throw new Exception($"Process still alive: {File.ReadAllText(path)}"); }
    static bool Alive(string path) { try { if (!File.Exists(path)) return false; using var p = Process.GetProcessById(int.Parse(File.ReadAllText(path))); return !p.HasExited; } catch (ArgumentException) { return false; } }
    public void Dispose() { foreach(var path in new[]{Parent,Child}) { try { if(Alive(path)) {using var p = Process.GetProcessById(int.Parse(File.ReadAllText(path))); p.Kill(true); p.WaitForExit(3000);} File.Delete(path); } catch {} } }
}
static class Native { [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint id); }









