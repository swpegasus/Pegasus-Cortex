using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Pegasus.AgentOps.Engine;

if(args.Length>0&&args[0]=="--argv-child")
{
    using var child=WindowsIdentity.GetCurrent();
    Console.WriteLine(JsonSerializer.Serialize(new{sid=child.User!.Value,session=Process.GetCurrentProcess().SessionId,administrator=new WindowsPrincipal(child).IsInRole(WindowsBuiltInRole.Administrator),argv=args[1..]}));
    return 0;
}
if(args is not [var workRoot,var expectedHelperSha,var expectedEngineVersion,var reportPath])return 2;
using var identity=WindowsIdentity.GetCurrent();
if(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))throw new InvalidOperationException("The reuse argv probe requires ordinary-user execution.");
var roleRoot=Path.GetFullPath(AppContext.BaseDirectory);
var enginePath=Path.GetFullPath(typeof(ProcessRunner).Assembly.Location);
if(!StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(enginePath),Path.TrimEndingDirectorySeparator(roleRoot))||FileVersionInfo.GetVersionInfo(enginePath).ProductVersion!=expectedEngineVersion)throw new InvalidOperationException("The candidate role engine was not loaded.");
var helper=Path.Combine(roleRoot,"Pegasus.AgentOps.ProcessHost.exe");
using(var stream=File.OpenRead(helper))if(Convert.ToHexStringLower(SHA256.HashData(stream))!=expectedHelperSha)throw new InvalidOperationException("The exact reused helper changed.");
var argv=new[]{"","plain","two words","quote\"here","C:\\space here\\","$(literal data)","a;b&c","λ字符"};
var dotnet=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"dotnet","dotnet.exe");
var environment=new Dictionary<string,string?>{{"SystemRoot",Environment.GetFolderPath(Environment.SpecialFolder.Windows)},{"WINDIR",Environment.GetFolderPath(Environment.SpecialFolder.Windows)},{"TEMP",workRoot},{"TMP",workRoot},{"PROCESSOR_ARCHITECTURE","AMD64"},{"DOTNET_ROOT",Path.GetDirectoryName(dotnet)},{"DOTNET_CLI_TELEMETRY_OPTOUT","1"},{"DOTNET_SKIP_FIRST_TIME_EXPERIENCE","1"}};
var result=await ProcessRunner.RunAsync(dotnet,new[]{typeof(Program).Assembly.Location,"--argv-child"}.Concat(argv).ToArray(),TimeSpan.FromSeconds(15),workRoot,ProcessEnvironment.Replace(environment),CancellationToken.None);
if(result.ExitCode!=0||result.TimedOut||result.StdoutTruncated||result.StderrTruncated||!string.IsNullOrEmpty(result.Stderr))throw new InvalidOperationException("Reused helper argv execution failed: exit="+result.ExitCode);
using var document=JsonDocument.Parse(result.Stdout);var childResult=document.RootElement;
if(childResult.GetProperty("sid").GetString()!=identity.User!.Value||childResult.GetProperty("session").GetInt32()!=Process.GetCurrentProcess().SessionId||childResult.GetProperty("administrator").GetBoolean()||!childResult.GetProperty("argv").EnumerateArray().Select(value=>value.GetString()).SequenceEqual(argv))throw new InvalidOperationException("Ordinary owner identity or literal argv changed across the reused helper.");
var report=JsonSerializer.Serialize(new{passed=true,tier="actual ordinary-user ProcessHost argv execution in candidate role closure",roleRoot,helper,helperSha256=expectedHelperSha,enginePath,engineVersion=expectedEngineVersion,sid=identity.User.Value,session=Process.GetCurrentProcess().SessionId,administrator=false,argumentCount=argv.Length,exit=result.ExitCode,actualAdministratorAcceptance=false});
using(var stream=new FileStream(reportPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read))using(var writer=new StreamWriter(stream))writer.Write(report);
Console.WriteLine(report);
return 0;
