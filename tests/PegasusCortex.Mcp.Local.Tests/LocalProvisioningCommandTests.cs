using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Pegasus.Cortex.Mcp.Local.Configuration;

internal static class LocalProvisioningCommandTests
{
    public static async Task ExplicitCreateOnlyProvisioningAsync()
    {
        await EmptyProfileCreateOnlyProvisioningAsync().ConfigureAwait(false);
        await SuccessAndLoadAsync().ConfigureAwait(false);
        await WindowsProjectRootAdmissionPolicyAsync().ConfigureAwait(false);
        await InvalidArgumentsFailBeforeCreationAsync().ConfigureAwait(false);
        await ExistingRootRemainsUntouchedAsync().ConfigureAwait(false);
        await PreCancelledProvisioningLeavesNoStateAsync().ConfigureAwait(false);
        await SimilarVerbDoesNotRouteAsync().ConfigureAwait(false);
    }

    private static async Task EmptyProfileCreateOnlyProvisioningAsync()
    {
        var fixture = CreateFixture();
        try
        {
            var arguments = ProfileArguments(fixture);
            var result = await LocalProcessHarness.RunAsync(arguments, [])
                .ConfigureAwait(false);
            LocalAssert.Equal(
                0,
                result.ExitCode,
                $"The explicit provision-profile verb failed: {result.StandardError}");
            LocalAssert.True(
                string.IsNullOrEmpty(result.StandardError),
                "Successful empty-profile provisioning emitted an error diagnostic.");

            var paths = CortexLocalProjectRegistryProvisioner.PortableStatePaths(
                fixture.ProfileId,
                fixture.StateRoot);
            LocalAssert.True(
                File.Exists(paths.RegistryPath),
                "Empty-profile provisioning did not create the protected registry.");
            LocalAssert.False(
                File.Exists(paths.DatabasePath),
                "Empty-profile provisioning unexpectedly initialized runtime storage.");
            var registry = await CortexLocalProjectRegistry.LoadAsync(
                paths.RegistryPath,
                fixture.ProfileId,
                CancellationToken.None).ConfigureAwait(false);
            LocalAssert.Equal(
                0,
                registry.Document.Projects.Count,
                "The provision-profile verb invented project authority.");
            LocalAssert.True(
                result.StandardOutput.Contains(
                    "\"projectCount\":0",
                    StringComparison.Ordinal),
                "Empty-profile provisioning did not report its zero-project result.");

            var registryBefore = await File.ReadAllBytesAsync(paths.RegistryPath)
                .ConfigureAwait(false);
            var repeated = await LocalProcessHarness.RunAsync(arguments, [])
                .ConfigureAwait(false);
            LocalAssert.Equal(
                1,
                repeated.ExitCode,
                "Repeated empty-profile provisioning did not fail create-only.");
            LocalAssert.True(
                repeated.StandardError.Contains(
                    "cortex_local_profile_provision_failed",
                    StringComparison.Ordinal),
                "Repeated empty-profile provisioning returned the wrong diagnostic.");
            var registryAfter = await File.ReadAllBytesAsync(paths.RegistryPath)
                .ConfigureAwait(false);
            LocalAssert.True(
                registryBefore.SequenceEqual(registryAfter),
                "Repeated empty-profile provisioning changed the protected registry.");

            var malformed = arguments[..^2];
            await LocalAssert.ThrowsAsync<ArgumentException>(
                () => Task.Run(() =>
                    CortexLocalProfileProvisionArguments.Parse(malformed)),
                "A missing empty-profile option was accepted.")
                .ConfigureAwait(false);
        }
        finally
        {
            DeleteFixture(fixture.ParentRoot);
        }
    }

    private static async Task SuccessAndLoadAsync()
    {
        var fixture = CreateFixture();
        try
        {
            var result = await LocalProcessHarness.RunAsync(
                Arguments(fixture),
                []).ConfigureAwait(false);
            LocalAssert.Equal(
                0,
                result.ExitCode,
                $"The explicit provision-project verb failed: {result.StandardError}");
            LocalAssert.True(
                string.IsNullOrEmpty(result.StandardError),
                "Successful provisioning emitted an error diagnostic.");
            LocalAssert.False(
                string.IsNullOrWhiteSpace(result.StandardOutput),
                "Successful provisioning did not emit its bounded result.");

            var paths = CortexLocalProjectRegistryProvisioner.PortableStatePaths(
                fixture.ProfileId,
                fixture.StateRoot);
            LocalAssert.True(
                File.Exists(paths.RegistryPath),
                "Successful provisioning did not create the protected registry.");
            LocalAssert.False(
                File.Exists(paths.DatabasePath),
                "Create-only project provisioning unexpectedly initialized runtime storage.");
            var registry = await CortexLocalProjectRegistry.LoadAsync(
                paths.RegistryPath,
                fixture.ProfileId,
                CancellationToken.None).ConfigureAwait(false);
            LocalAssert.Equal(
                1,
                registry.Document.Projects.Count,
                "The provision-project verb did not register exactly one project.");
            var project = registry.Document.Projects.Single();
            LocalAssert.Equal(
                LocalTestContext.TenantId,
                project.TenantId,
                "Provisioning changed the tenant identifier.");
            LocalAssert.Equal(
                LocalTestContext.PrimaryProjectLinkId,
                project.ProjectLinkId,
                "Provisioning changed the project-link identifier.");
            LocalAssert.Equal(
                LocalTestContext.PrimaryProjectId,
                project.ProjectId,
                "Provisioning changed the project identifier.");
            LocalAssert.Equal(
                LocalTestContext.PrimaryNamespace,
                project.ProjectNamespace,
                "Provisioning changed the project namespace.");
            LocalAssert.Equal(
                fixture.ProjectRoot,
                project.RootPath,
                "Provisioning changed the canonical project root.");
            LocalAssert.True(
                project.AllowRead && project.AllowWrite,
                "Provisioning changed the explicit read/write grants.");
        }
        finally
        {
            DeleteFixture(fixture.ParentRoot);
        }
    }

    private static async Task WindowsProjectRootAdmissionPolicyAsync()
    {
        if (!OperatingSystem.IsWindows())
            return;
        await WindowsProjectRootAdmissionPolicyOnWindowsAsync().ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task WindowsProjectRootAdmissionPolicyOnWindowsAsync()
    {
        CurrentUserOwnedRootIsAccepted();
        SafeGroupOwnedRootIsAccepted();
        InheritedCollaborativeRootIsAccepted();
        await ExplicitBroadWriterIsRejectedAsync().ConfigureAwait(false);
        await UntrustedWritableRootFailsBeforeStateCreationAsync().ConfigureAwait(false);
        await ReparseRootEntryIsRejectedAsync().ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static void CurrentUserOwnedRootIsAccepted()
    {
        var fixture = CreateFixture();
        try
        {
            LocalAssert.Equal(
                fixture.ProjectRoot,
                CortexLocalRegistryPathPolicy.ValidateProjectRoot(fixture.ProjectRoot),
                "A canonical current-user-owned project root was rejected.");
        }
        finally
        {
            DeleteFixture(fixture.ParentRoot);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SafeGroupOwnedRootIsAccepted()
    {
        var currentUser = TestAccountSid(4101);
        var projectGroup = TestAccountSid(4102);
        var security = NewProjectSecurity(projectGroup);
        security.AddAccessRule(new FileSystemAccessRule(
            projectGroup,
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        CortexLocalRegistryAclPolicy.RequireWindowsProjectRootAcl(
            security,
            currentUser,
            SidSet(currentUser, projectGroup),
            SidSet(currentUser, projectGroup));
    }

    [SupportedOSPlatform("windows")]
    private static void InheritedCollaborativeRootIsAccepted()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var parentRoot = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), $"pcx-collab-{suffix}"));
        Directory.CreateDirectory(parentRoot);
        SetWindowsCollaborativeWorkspaceAcl(parentRoot);
        var projectRoot = Path.Combine(parentRoot, "project");
        Directory.CreateDirectory(projectRoot);
        try
        {
            LocalAssert.Equal(
                projectRoot,
                CortexLocalRegistryPathPolicy.ValidateProjectRoot(projectRoot),
                "A collaborative project root with a matching inherited parent rule was rejected.");
        }
        finally
        {
            DeleteFixture(parentRoot);
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task ExplicitBroadWriterIsRejectedAsync()
    {
        var currentUser = TestAccountSid(4201);
        var authenticatedUsers = new SecurityIdentifier(
            WellKnownSidType.AuthenticatedUserSid,
            domainSid: null);
        var security = NewProjectSecurity(currentUser);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            authenticatedUsers,
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        await LocalAssert.ThrowsAsync<UnauthorizedAccessException>(
            () => Task.Run(() => CortexLocalRegistryAclPolicy.RequireWindowsProjectRootAcl(
                security,
                currentUser,
                SidSet(currentUser, authenticatedUsers),
                SidSet(currentUser, authenticatedUsers))),
            "An explicit broad project writer was accepted without an inherited workspace boundary.")
            .ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task UntrustedWritableRootFailsBeforeStateCreationAsync()
    {
        var fixture = CreateFixture();
        var untrusted = TestAccountSid(4309);
        var projectInfo = new DirectoryInfo(fixture.ProjectRoot);
        var security = projectInfo.GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        security.AddAccessRule(new FileSystemAccessRule(
            untrusted,
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        projectInfo.SetAccessControl(security);
        try
        {
            await LocalAssert.ThrowsAsync<UnauthorizedAccessException>(
                () => CortexLocalProjectRegistryProvisioner.CreateNewPortableAsync(
                    fixture.ProfileId,
                    fixture.StateRoot,
                    [new CortexLocalProjectRegistrationRequest(
                        LocalTestContext.TenantId,
                        LocalTestContext.PrimaryProjectLinkId,
                        LocalTestContext.PrimaryProjectId,
                        LocalTestContext.PrimaryNamespace,
                        fixture.ProjectRoot,
                        AllowRead: true,
                        AllowWrite: true)],
                    CancellationToken.None).AsTask(),
                "A project root writable by an unrelated principal was accepted.")
                .ConfigureAwait(false);
            LocalAssert.False(
                Directory.Exists(fixture.StateRoot) || File.Exists(fixture.StateRoot),
                "Rejected project-root admission created local state.");
            LocalAssert.False(
                Directory.EnumerateFileSystemEntries(fixture.ProjectRoot).Any(),
                "Rejected project-root admission wrote into the selected project.");
        }
        finally
        {
            DeleteFixture(fixture.ParentRoot);
        }
    }

    private static async Task ReparseRootEntryIsRejectedAsync()
    {
        await LocalAssert.ThrowsAsync<IOException>(
            () => Task.Run(() =>
                CortexLocalRegistryPathPolicy.RequireOrdinaryProjectRootEntry(
                    FileAttributes.Directory | FileAttributes.ReparsePoint,
                    linkTarget: null)),
            "A reparse-point project root entry was accepted.")
            .ConfigureAwait(false);
        await LocalAssert.ThrowsAsync<IOException>(
            () => Task.Run(() =>
                CortexLocalRegistryPathPolicy.RequireOrdinaryProjectRootEntry(
                    FileAttributes.Directory,
                    linkTarget: @"C:\target")),
            "A linked project root entry was accepted.")
            .ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity NewProjectSecurity(SecurityIdentifier owner)
    {
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier TestAccountSid(int relativeId) =>
        new($"S-1-5-21-111111111-222222222-333333333-{relativeId}");

    [SupportedOSPlatform("windows")]
    private static IReadOnlySet<string> SidSet(params SecurityIdentifier[] sids) =>
        new HashSet<string>(sids.Select(sid => sid.Value), StringComparer.Ordinal);

    private static async Task InvalidArgumentsFailBeforeCreationAsync()
    {
        var fixture = CreateFixture();
        try
        {
            var valid = Arguments(fixture);
            await AssertParseRejectedAsync(
                valid[..^2],
                "A missing required provisioning option was accepted.")
                .ConfigureAwait(false);

            var duplicate = valid.ToArray();
            duplicate[3] = "--profile";
            await AssertParseRejectedAsync(
                duplicate,
                "A duplicate provisioning option was accepted.")
                .ConfigureAwait(false);

            var unknown = valid.ToArray();
            unknown[3] = "--unsupported";
            await AssertParseRejectedAsync(
                unknown,
                "An unknown provisioning option was accepted.")
                .ConfigureAwait(false);

            var invalidBoolean = ReplaceValue(valid, "--allow-read", "yes");
            await AssertParseRejectedAsync(
                invalidBoolean,
                "A non-canonical boolean grant was accepted.")
                .ConfigureAwait(false);

            var noGrants = ReplaceValue(
                ReplaceValue(valid, "--allow-read", "false"),
                "--allow-write",
                "false");
            await AssertParseRejectedAsync(
                noGrants,
                "A project registration with no allowed operation was accepted.")
                .ConfigureAwait(false);

            var invalidProjectId = ReplaceValue(
                valid,
                "--project-id",
                "invalid project id");
            await AssertParseRejectedAsync(
                invalidProjectId,
                "An invalid project identifier was accepted.")
                .ConfigureAwait(false);

            var processResult = await LocalProcessHarness.RunAsync(
                invalidBoolean,
                []).ConfigureAwait(false);
            LocalAssert.Equal(
                2,
                processResult.ExitCode,
                "Invalid provisioning arguments did not use the argument-error exit code.");
            LocalAssert.True(
                processResult.StandardError.Contains(
                    "cortex_local_project_provision_arguments_invalid",
                    StringComparison.Ordinal),
                "Invalid provisioning arguments did not use the provisioning diagnostic.");
            LocalAssert.False(
                Directory.Exists(fixture.StateRoot) || File.Exists(fixture.StateRoot),
                "Invalid provisioning arguments created state.");
        }
        finally
        {
            DeleteFixture(fixture.ParentRoot);
        }
    }

    private static async Task ExistingRootRemainsUntouchedAsync()
    {
        var fixture = CreateFixture();
        Directory.CreateDirectory(fixture.StateRoot);
        var markerPath = Path.Combine(fixture.StateRoot, "owner-data.bin");
        var marker = new byte[] { 2, 4, 6, 8, 10 };
        await File.WriteAllBytesAsync(markerPath, marker).ConfigureAwait(false);
        var securityBefore = CaptureDirectorySecurity(fixture.StateRoot);
        try
        {
            var result = await LocalProcessHarness.RunAsync(
                Arguments(fixture),
                []).ConfigureAwait(false);
            LocalAssert.Equal(
                1,
                result.ExitCode,
                "Create-only provisioning did not refuse a preexisting state root.");
            LocalAssert.True(
                result.StandardError.Contains(
                    "cortex_local_project_provision_failed",
                    StringComparison.Ordinal),
                "An occupied state root did not return the provisioning failure diagnostic.");
            var markerAfter = await File.ReadAllBytesAsync(markerPath)
                .ConfigureAwait(false);
            LocalAssert.True(
                marker.SequenceEqual(markerAfter),
                "Failed provisioning changed the preexisting marker bytes.");
            LocalAssert.Equal(
                securityBefore,
                CaptureDirectorySecurity(fixture.StateRoot),
                "Failed provisioning changed the preexisting state-root security.");
            LocalAssert.Equal(
                1,
                Directory.EnumerateFileSystemEntries(fixture.StateRoot).Count(),
                "Failed provisioning added content to the preexisting state root.");
        }
        finally
        {
            DeleteFixture(fixture.ParentRoot);
        }
    }

    private static async Task PreCancelledProvisioningLeavesNoStateAsync()
    {
        var fixture = CreateFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await LocalAssert.ThrowsAsync<OperationCanceledException>(
                () => CortexLocalProjectRegistryProvisioner.CreateNewPortableAsync(
                    fixture.ProfileId,
                    fixture.StateRoot,
                    [new CortexLocalProjectRegistrationRequest(
                        LocalTestContext.TenantId,
                        LocalTestContext.PrimaryProjectLinkId,
                        LocalTestContext.PrimaryProjectId,
                        LocalTestContext.PrimaryNamespace,
                        fixture.ProjectRoot,
                        AllowRead: true,
                        AllowWrite: true)],
                    cancellation.Token).AsTask(),
                "A pre-cancelled create-only provisioning operation was accepted.");
            LocalAssert.False(
                Directory.Exists(fixture.StateRoot) || File.Exists(fixture.StateRoot),
                "Cancelled provisioning left a state root behind.");
        }
        finally
        {
            DeleteFixture(fixture.ParentRoot);
        }
    }

    private static async Task SimilarVerbDoesNotRouteAsync()
    {
        var fixture = CreateFixture();
        try
        {
            var arguments = Arguments(fixture);
            arguments[0] = "provision-project-extra";
            var result = await LocalProcessHarness.RunAsync(arguments, [])
                .ConfigureAwait(false);
            LocalAssert.Equal(
                2,
                result.ExitCode,
                "A similar non-literal verb was routed to project provisioning.");
            LocalAssert.False(
                Directory.Exists(fixture.StateRoot) || File.Exists(fixture.StateRoot),
                "A similar non-literal verb created local state.");
        }
        finally
        {
            DeleteFixture(fixture.ParentRoot);
        }
    }

    private static async Task AssertParseRejectedAsync(
        string[] arguments,
        string message) =>
        await LocalAssert.ThrowsAsync<ArgumentException>(
            () => Task.Run(() =>
                CortexLocalProjectProvisionArguments.Parse(arguments)),
            message).ConfigureAwait(false);

    private static string[] Arguments(ProvisioningFixture fixture) =>
    [
        "provision-project",
        "--profile",
        fixture.ProfileId,
        "--state-root",
        fixture.StateRoot,
        "--tenant-id",
        LocalTestContext.TenantId,
        "--project-link-id",
        LocalTestContext.PrimaryProjectLinkId,
        "--project-id",
        LocalTestContext.PrimaryProjectId,
        "--project-namespace",
        LocalTestContext.PrimaryNamespace,
        "--project-root",
        fixture.ProjectRoot,
        "--allow-read",
        "true",
        "--allow-write",
        "true"
    ];

    private static string[] ProfileArguments(ProvisioningFixture fixture) =>
    [
        "provision-profile",
        "--profile",
        fixture.ProfileId,
        "--state-root",
        fixture.StateRoot
    ];

    private static string[] ReplaceValue(
        IReadOnlyList<string> source,
        string option,
        string value)
    {
        var result = source.ToArray();
        var index = Array.IndexOf(result, option);
        if (index < 0 || index + 1 >= result.Length)
            throw new InvalidOperationException($"The fixture has no '{option}' value.");
        result[index + 1] = value;
        return result;
    }

    private static ProvisioningFixture CreateFixture()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var parentRoot = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), $"pcx-provision-{suffix}"));
        Directory.CreateDirectory(parentRoot);
        if (OperatingSystem.IsWindows())
            ProtectWindowsTestWorkspace(parentRoot);
        var projectRoot = Path.Combine(parentRoot, "project");
        Directory.CreateDirectory(projectRoot);
        var profileId = $"provision-{suffix}";
        return new ProvisioningFixture(
            parentRoot,
            profileId,
            Path.Combine(parentRoot, profileId),
            projectRoot);
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectWindowsTestWorkspace(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User ??
            throw new InvalidOperationException("The current Windows test user SID is unavailable.");
        var security = NewProjectSecurity(currentUser);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindowsCollaborativeWorkspaceAcl(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var currentUser = identity.User ??
            throw new InvalidOperationException("The current Windows test user SID is unavailable.");
        var security = NewProjectSecurity(currentUser);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, domainSid: null),
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void DeleteFixture(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static string CaptureDirectorySecurity(string path) =>
        OperatingSystem.IsWindows()
            ? CaptureWindowsDirectorySecurity(path)
            : File.GetUnixFileMode(path).ToString();

    [SupportedOSPlatform("windows")]
    private static string CaptureWindowsDirectorySecurity(string path)
    {
        const AccessControlSections sections =
            AccessControlSections.Access |
            AccessControlSections.Owner |
            AccessControlSections.Group;
        return new DirectoryInfo(path)
            .GetAccessControl(sections)
            .GetSecurityDescriptorSddlForm(sections);
    }

    private sealed record ProvisioningFixture(
        string ParentRoot,
        string ProfileId,
        string StateRoot,
        string ProjectRoot);
}
