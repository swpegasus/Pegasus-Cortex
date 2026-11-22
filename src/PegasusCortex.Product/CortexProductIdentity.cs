using System.Text.RegularExpressions;

namespace Pegasus.Cortex.Product;

public static partial class CortexProductIdentity
{
    public const string Product = "Pegasus Cortex";
    public const string Channel = "Prod Alpha";
    public const string CompanyDirectory = "Pegasus";
    public const string ProductDirectory = "Cortex";
    public const string ServiceName = "Pegasus Cortex Execution";
    public const string CatalogContract = "pegasus-cortex-product-catalog";
    public const int CatalogVersion = 1;
    public const string ExecutionProtocol = "1.0";
    public const int PaoSchema = 3;
    public const string UserExecutionPipePrefix = "pegasus-cortex-user-execution-v1";
    public const string UserMcpPipePrefix = "pegasus-cortex-mcp-v1";
    public const string UserNativePipePrefix = "pegasus-cortex-native-v1";
    public const string ExecutionUserPipe = "pegasus-cortex-execution-user-v1";
    public const string ExecutionSystemPipe = "pegasus-cortex-execution-system-v1";
    public const string ApplicationAuthorizationPipe = "pegasus-cortex-application-system-v1";
    public const string HostRegistrationPipe = "pegasus-cortex-host-register-v1";
    public const string NsisVersion = "3.12";
    public const string NsisArchiveSha256 = "56581f90db321581c5381193d796fffcf2d24b2f8fed2160a6c6a3baa67f2c4f";
    public const string NsisCompilerRelativePath = "tools/nsis/3.12/Bin/makensis.exe";
    public const string NsisAdmissionRelativePath = "tools/nsis/3.12/pegasus-nsis-admission.v1.json";
    public const string NsisUpstreamCompilerSha256 = "25d1aa7081db1a9de9690b59983f9652b7409a189c3444328cc1841eff693e8d";
    public const string ProdAlphaRootThumbprint = "C5A084A372E3FD021A91C3B8A17F4C0F14C0A8CB";
    public const string ProdAlphaPublisherThumbprint = "6AE355621D021F7189D9511488DB4A8CB6240F12";

    public static string UserExecutionPipe(int session)
    {
        if (session <= 0)
            throw new ArgumentOutOfRangeException(nameof(session), "A signed-in Windows session is required.");
        return $"{UserExecutionPipePrefix}-{session}";
    }

    public static string UserMcpPipe(int session)
    {
        if (session <= 0) throw new ArgumentOutOfRangeException(nameof(session));
        return $"{UserMcpPipePrefix}-{session}";
    }

    public static string UserNativePipe(int session)
    {
        if (session <= 0) throw new ArgumentOutOfRangeException(nameof(session));
        return $"{UserNativePipePrefix}-{session}";
    }

    public static bool IsReleaseVersion(string? value) =>
        value is not null && ReleaseVersionPattern().IsMatch(value);

    [GeneratedRegex(
        @"^[12]\.\d{4}\.\d{2}\.\d{2}\.\d{2}\.\d{2}\.[A-D]$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseVersionPattern();
}
