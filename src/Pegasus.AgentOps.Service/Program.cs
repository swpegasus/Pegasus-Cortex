using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Pegasus.AgentOps.Engine;
using Pegasus.Cortex.Product;

#if DEBUG
if(args.Contains("--console-fixture",StringComparer.Ordinal))
{
    var current=LocalIdentity.Current();
    var state=Path.GetFullPath(Environment.GetEnvironmentVariable("PAO_STATE_ROOT")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Pegasus","AgentOps","service-test"));
    var ownerSid=Environment.GetEnvironmentVariable("PAO_OWNER_SID")??current.Sid;
    await ServiceRuntime.RunAsync(new ServiceConfiguration(ownerSid,state,IsFixture:true),string.Equals(Environment.GetEnvironmentVariable("PAO_TEST_CRASH_AFTER_TRAY"),"1",StringComparison.Ordinal),CancellationToken.None);
    return 0;
}

if(args.Length==3&&args[0]=="--validate-config-fixture")
{
    try{_ = ServiceConfigurationLoader.Load(Path.GetFullPath(args[2]),Path.GetFullPath(args[1]));return 0;}
    catch(ServiceConfigurationException ex){Console.Error.WriteLine($"err={ex.Code}{(ex.Entry is null?string.Empty:" entry="+Uri.EscapeDataString(ex.Entry))}");return 4;}
}
if(args.Length==2&&args[0]=="--prepare-config-fixture")
{
    try{ServiceStateSecurity.RestrictFixture(Path.GetFullPath(args[1]));return 0;}
    catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ServiceConfigurationException){Console.Error.WriteLine("err=service_config_invalid");return 4;}
}
#endif

try
{
    // Connect the generic host to the Service Control Manager before touching
    // protected product state. On a cold self-contained launch, repeated ACL
    // validation and trace-file writes here can consume the SCM's startup
    // window and produce error 1053 even though the worker is healthy.
    var builder=Host.CreateApplicationBuilder(args);
    builder.Services.AddHostedService<AgentOpsWindowsWorker>();
    builder.Services.AddWindowsService(options=>options.ServiceName=CortexProductIdentity.ServiceName);
    var host=builder.Build();
    await host.RunAsync();
    return Environment.ExitCode;
}
catch(Exception ex)
{
    ServiceStartupTrace.Write(ServiceStartupTrace.TryResolveRoot(),"host-failed",ex.GetType().Name);
    throw;
}
