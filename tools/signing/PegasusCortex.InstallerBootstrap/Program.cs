using System.Diagnostics;

namespace Pegasus.Cortex.InstallerBootstrap;

internal static class Program
{
    private const int InvalidPackageExitCode = 3;
    private const string PublisherThumbprint = "6AE355621D021F7189D9511488DB4A8CB6240F12";
    private const string RootThumbprint = "C5A084A372E3FD021A91C3B8A17F4C0F14C0A8CB";
    private const string PackageVerifierCommand = """
        $ErrorActionPreference = 'Stop'
        $root = [IO.Path]::GetFullPath($env:PEGASUS_BOOTSTRAP_PACKAGE_ROOT)
        $manifestPath = Join-Path $root 'package-manifest.json'
        $signaturePath = Join-Path $root 'package-manifest.p7s'
        $rootCertificatePath = Join-Path $root 'trust/pegasus-prod-alpha-root.cer'
        $publisherCertificatePath = Join-Path $root 'trust/pegasus-prod-alpha-publisher.cer'
        foreach ($path in @($manifestPath, $signaturePath, $rootCertificatePath, $publisherCertificatePath)) {
            $item = Get-Item -LiteralPath $path -Force
            if (-not $item.PSIsContainer -and (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0)) { continue }
            exit 50
        }
        $rootCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($rootCertificatePath)
        $publisherCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($publisherCertificatePath)
        $normalize = { param([string]$value) $value.Replace(' ', '').ToUpperInvariant() }
        if ((& $normalize $rootCertificate.Thumbprint) -cne $env:PEGASUS_BOOTSTRAP_ROOT_THUMBPRINT) { exit 51 }
        if ((& $normalize $publisherCertificate.Thumbprint) -cne $env:PEGASUS_BOOTSTRAP_PUBLISHER_THUMBPRINT) { exit 52 }
        $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
        try {
            $chain.ChainPolicy.TrustMode = [Security.Cryptography.X509Certificates.X509ChainTrustMode]::CustomRootTrust
            $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
            $chain.ChainPolicy.VerificationFlags = [Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
            $chain.ChainPolicy.CustomTrustStore.Add($rootCertificate)
            if (-not $chain.Build($publisherCertificate)) { exit 53 }
            if ($chain.ChainElements.Count -ne 2) { exit 54 }
        }
        finally { $chain.Dispose() }
        $manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
        $signatureBytes = [IO.File]::ReadAllBytes($signaturePath)
        $contentInfo = [Security.Cryptography.Pkcs.ContentInfo]::new($manifestBytes)
        $cms = [Security.Cryptography.Pkcs.SignedCms]::new($contentInfo, $true)
        try { $cms.Decode($signatureBytes); $cms.CheckSignature($true) } catch { exit 55 }
        if ($cms.SignerInfos.Count -ne 1) { exit 56 }
        if ((& $normalize $cms.SignerInfos[0].Certificate.Thumbprint) -cne $env:PEGASUS_BOOTSTRAP_PUBLISHER_THUMBPRINT) { exit 57 }
        try { $manifest = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json -Depth 20 -ErrorAction Stop } catch { exit 58 }
        if ([string]$manifest.schema -cne 'pegasus.cortex.local-operational-package/1.2' -or [string]$manifest.role -cne 'installer' -or [string]$manifest.channel -cne 'Prod Alpha') { exit 59 }
        if ([string]$manifest.signing.publisherThumbprint -cne $env:PEGASUS_BOOTSTRAP_PUBLISHER_THUMBPRINT -or [string]$manifest.signing.rootThumbprint -cne $env:PEGASUS_BOOTSTRAP_ROOT_THUMBPRINT) { exit 60 }
        $expected = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        foreach ($entry in @($manifest.files)) {
            $relative = [string]$entry.path
            if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Contains('\') -or $relative.Contains(':') -or [IO.Path]::IsPathRooted($relative)) { exit 61 }
            $segments = $relative.Split('/')
            if (@($segments | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) { exit 62 }
            if ($expected.ContainsKey($relative)) { exit 63 }
            $expected.Add($relative, $entry)
        }
        $actual = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($file in @(Get-ChildItem -LiteralPath $root -Recurse -File -Force)) {
            if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { exit 64 }
            $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
            if ($relative -in @('package-manifest.json', 'package-manifest.p7s')) { continue }
            if (-not $expected.ContainsKey($relative) -or -not $actual.Add($relative)) { exit 65 }
            $entry = $expected[$relative]
            if ([long]$entry.bytes -ne $file.Length) { exit 66 }
            $hash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($file.FullName)))
            if ($hash -cne [string]$entry.sha256) { exit 67 }
        }
        if ($actual.Count -ne $expected.Count) { exit 68 }
        exit 0
        """;
    private const string SignatureVerifierCommand = """
        $ErrorActionPreference = 'Stop'
        $signature = Get-AuthenticodeSignature -LiteralPath $env:PEGASUS_BOOTSTRAP_SIGNATURE_PATH
        if ([string]$signature.SignatureType -cne 'Authenticode') { exit 40 }
        if ($null -eq $signature.SignerCertificate) { exit 41 }
        $actual = ([string]$signature.SignerCertificate.Thumbprint).Replace(' ', '').ToUpperInvariant()
        if ($actual -cne $env:PEGASUS_BOOTSTRAP_PUBLISHER_THUMBPRINT) { exit 42 }
        if ([string]$signature.Status -notin @('Valid', 'UnknownError', 'NotTrusted')) { exit 43 }
        exit 0
        """;

    public static int Main(string[] args)
    {
        try
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Installer executable path is unavailable.");
            var root = Path.GetDirectoryName(Path.GetFullPath(executable))
                ?? throw new InvalidOperationException("Installer package root is unavailable.");
            AssertOrdinaryDirectory(root);

            var script = Path.Combine(root, "Install-PegasusCortexV2X64.ps1");
            AssertOrdinaryFile(script, root);
            var trustScript = Path.Combine(root, "Invoke-PegasusProdAlphaTrust.ps1");
            AssertOrdinaryFile(trustScript, root);
            var signingModule = Path.Combine(root, "PegasusSigning.psm1");
            AssertOrdinaryFile(signingModule, root);
            var trustModule = Path.Combine(root, "PegasusTrust.psm1");
            AssertOrdinaryFile(trustModule, root);
            var programFiles = Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolderOption.DoNotVerify);
            var pwsh = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
            AssertOrdinaryFile(pwsh, Path.GetDirectoryName(pwsh)!);

            VerifyPackageManifest(pwsh, root);
            foreach (var signedPayload in new[] { script, trustScript, signingModule, trustModule })
            {
                VerifyAuthenticodePayload(pwsh, signedPayload);
            }

            var start = new ProcessStartInfo
            {
                FileName = pwsh,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("-NoLogo");
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(script);
            foreach (var argument in args)
            {
                start.ArgumentList.Add(argument);
            }

            using var child = Process.Start(start)
                ?? throw new InvalidOperationException("PowerShell 7 installer process did not start.");
            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Pegasus Cortex installer bootstrap failed: {exception.Message}");
            return InvalidPackageExitCode;
        }
    }

    private static void VerifyPackageManifest(string pwsh, string root)
    {
        var start = new ProcessStartInfo
        {
            FileName = pwsh,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["PEGASUS_BOOTSTRAP_PACKAGE_ROOT"] = root;
        start.Environment["PEGASUS_BOOTSTRAP_ROOT_THUMBPRINT"] = RootThumbprint;
        start.Environment["PEGASUS_BOOTSTRAP_PUBLISHER_THUMBPRINT"] = PublisherThumbprint;
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(PackageVerifierCommand);

        using var child = Process.Start(start)
            ?? throw new InvalidOperationException("PowerShell package verifier did not start.");
        child.WaitForExit();
        if (child.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Installer package verification failed with code {child.ExitCode}.");
        }
    }

    private static void VerifyAuthenticodePayload(string pwsh, string path)
    {
        var start = new ProcessStartInfo
        {
            FileName = pwsh,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.Environment["PEGASUS_BOOTSTRAP_SIGNATURE_PATH"] = path;
        start.Environment["PEGASUS_BOOTSTRAP_PUBLISHER_THUMBPRINT"] = PublisherThumbprint;
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(SignatureVerifierCommand);

        using var child = Process.Start(start)
            ?? throw new InvalidOperationException("PowerShell signature verifier did not start.");
        child.WaitForExit();
        if (child.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Installer payload signature verification failed with code {child.ExitCode}.");
        }
    }

    private static void AssertOrdinaryDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"Expected an ordinary directory: '{path}'.");
        }
    }

    private static void AssertOrdinaryFile(string path, string expectedParent)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException("File parent is unavailable.");
        if (!string.Equals(parent, Path.GetFullPath(expectedParent), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"File escaped its expected package directory: '{full}'.");
        }
        var info = new FileInfo(full);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"Expected an ordinary file: '{full}'.");
        }
    }
}
