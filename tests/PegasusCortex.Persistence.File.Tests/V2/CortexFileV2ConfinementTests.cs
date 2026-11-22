using System.Text;
using IoFile = System.IO.File;
using System.Security.AccessControl;
using Pegasus.Cortex.Persistence.File.V2;
using Pegasus.Cortex.RootedIo;
using Pegasus.Cortex.Tests.IO;

namespace Pegasus.Cortex.Persistence.File.Tests.V2;

internal static class CortexFileV2ConfinementTests
{
    internal static async Task ParentSwapsAsync()
    {
        foreach (var kind in DirectorySwapFixture.AliasKinds)
        {
            using var scope = new DirectorySwapFixture();
            if (!scope.ProbeAlias(kind)) continue;
            await ParentSwap(scope, kind, createOnly: false).ConfigureAwait(false);
            await ParentSwap(scope, kind, createOnly: true).ConfigureAwait(false);
            await ReadSwap(scope, kind).ConfigureAwait(false);
        }
    }

    private static async Task ParentSwap(DirectorySwapFixture scope, string kind, bool createOnly)
    {
        var directory = Path.Combine(scope.Project, createOnly ? "manifest" : "hot");
        var retained = directory + "-retained";
        Directory.CreateDirectory(directory);
        var name = createOnly ? "task.json" : "HOT.json";
        var target = Path.Combine(directory, name);
        var outside = Path.Combine(scope.Outside, name);
        IoFile.WriteAllText(outside, "outside-sentinel");
        if (!createOnly) IoFile.WriteAllText(target, "old");
        var previous = RootedDirectory.TestObserver;
        var attempted = false; var swapped = false; Exception? failure = null;
        RootedDirectory.TestObserver = (point, path) =>
        {
            if (attempted || point != "parent_pinned" || !path.EndsWith(".tmp", StringComparison.Ordinal)) return;
            attempted = true; swapped = scope.TrySwap(directory, retained, kind);
        };
        try
        {
            var bytes = Encoding.UTF8.GetBytes("new");
            if (createOnly) await CortexFileV2AtomicIo.WriteCreateOnlyAtomicAsync(target, bytes, 64, scope.Project, default).ConfigureAwait(false);
            else await CortexFileV2AtomicIo.WriteAtomicAsync(target, bytes, 64, scope.Project, default).ConfigureAwait(false);
        }
        catch (IOException error) { failure = error; }
        finally { RootedDirectory.TestObserver = previous; scope.Restore(directory, retained); }
        Require(attempted, "The parent-swap barrier was not reached.");
        Require(IoFile.ReadAllText(outside) == "outside-sentinel", "The write changed the outside target.");
        if (swapped)
        {
            Require(failure is not null, "A replaced named parent was accepted.");
            Require(createOnly ? !IoFile.Exists(target) : IoFile.ReadAllText(target) == "old", "A refused write changed the original document.");
        }
        else
        {
            Require(OperatingSystem.IsWindows() && failure is null && IoFile.ReadAllText(target) == "new",
                "The held Windows parent did not both refuse rename and permit the original write. " + Diagnostic(attempted, swapped, failure));
        }
        Require(!Directory.EnumerateFiles(scope.Outside, "*.tmp").Any(), "Temporary data escaped the admitted parent.");
    }

    private static async Task ReadSwap(DirectorySwapFixture scope, string kind)
    {
        var directory = Path.Combine(scope.Project, "read"); var retained = directory + "-retained";
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "record.json");
        IoFile.WriteAllText(target, "original"); IoFile.WriteAllText(Path.Combine(scope.Outside, "record.json"), "outside-body");
        var attempted = false; var swapped = false; byte[]? result = null; Exception? failure = null;
        var previous = RootedDirectory.TestObserver;
        RootedDirectory.TestObserver = (point, path) =>
        {
            if (attempted || point != "root_pinned" || path != directory) return;
            attempted = true; swapped = scope.TrySwap(directory, retained, kind);
        };
        try { result = await CortexFileV2AtomicIo.ReadExactAsync(target, 64, default).ConfigureAwait(false); }
        catch (IOException error) { failure = error; }
        finally { RootedDirectory.TestObserver = previous; scope.Restore(directory, retained); }
        Require(attempted && (swapped ? failure is not null && result is null
            : OperatingSystem.IsWindows() && failure is null && Encoding.UTF8.GetString(result!) == "original"),
            "The read followed a replaced ancestor or failed to prove its original object. " + Diagnostic(attempted, swapped, failure));
    }

    internal static async Task PublicationAndCancellationAsync()
    {
        await WindowsReplacementConflictAsync().ConfigureAwait(false);
        foreach (var kind in DirectorySwapFixture.AliasKinds)
        {
            using var scope = new DirectorySwapFixture();
            if (!scope.ProbeAlias(kind)) continue;
            var target = Path.Combine(scope.Project, "HOT.json");
            var outside = Path.Combine(scope.Outside, "sentinel");
            IoFile.WriteAllText(target, "old"); IoFile.WriteAllText(outside, "outside");
            var previous = RootedDirectory.TestObserver;
            var attempted = false; var swapped = false; string? original = null; string? retained = null;
            Exception? failure = null;
            RootedDirectory.TestObserver = (point, path) =>
            {
                if (attempted || point != "before_publish") return;
                attempted = true; original = path; retained = Path.Combine(scope.Project, "retained-temporary");
                try { IoFile.Move(path, retained); }
                catch (IOException) when (OperatingSystem.IsWindows()) { return; }
                swapped = true; IoFile.CreateSymbolicLink(path, outside);
            };
            try { await CortexFileV2AtomicIo.WriteAtomicAsync(target, Encoding.UTF8.GetBytes("new"), 64, scope.Project, default).ConfigureAwait(false); }
            catch (IOException error) { failure = error; }
            finally
            {
                RootedDirectory.TestObserver = previous;
                if (swapped) { scope.RemoveAlias(original!); IoFile.Move(retained!, original!); }
            }
            Require(attempted && IoFile.ReadAllText(outside) == "outside", "Publication/cleanup touched the outside sentinel.");
            Require(swapped ? failure is not null && IoFile.ReadAllText(target) == "old"
                : OperatingSystem.IsWindows() && failure is null && IoFile.ReadAllText(target) == "new",
                "Publication adopted a substituted temporary file or falsely completed cleanup. " + Diagnostic(attempted, swapped, failure));
        }

        using var ordinary = new DirectorySwapFixture();
        var manifest = Path.Combine(ordinary.Project, "task.json");
        var body = Encoding.UTF8.GetBytes("{\"task\":\"synthetic\"}");
        await CortexFileV2AtomicIo.WriteCreateOnlyAtomicAsync(manifest, body, 128, ordinary.Project, default).ConfigureAwait(false);
        await CortexFileV2AtomicIo.WriteCreateOnlyAtomicAsync(manifest, body, 128, ordinary.Project, default).ConfigureAwait(false);
        Require((await CortexFileV2AtomicIo.ReadExactAsync(manifest, 128, default).ConfigureAwait(false)).SequenceEqual(body),
            "Create-only replay did not retain exact bytes.");
        try
        {
            await CortexFileV2AtomicIo.WriteCreateOnlyAtomicAsync(manifest, Encoding.UTF8.GetBytes("different"), 128, ordinary.Project, default).ConfigureAwait(false);
            throw new InvalidOperationException("Conflicting immutable manifest bytes were accepted.");
        }
        catch (InvalidDataException) { }
        using var cancellation = new CancellationTokenSource();
        var old = RootedDirectory.TestObserver;
        RootedDirectory.TestObserver = (point, path) =>
        { if (point == "entry_opened" && path.EndsWith(".tmp", StringComparison.Ordinal)) cancellation.Cancel(); };
        try
        {
            await CortexFileV2AtomicIo.WriteAtomicAsync(manifest, Encoding.UTF8.GetBytes("cancelled"), 128,
                ordinary.Project, cancellation.Token).ConfigureAwait(false);
            throw new InvalidOperationException("Cancellation after temporary creation was ignored.");
        }
        catch (OperationCanceledException) { }
        finally { RootedDirectory.TestObserver = old; }
        Require(IoFile.ReadAllBytes(manifest).SequenceEqual(body) && !Directory.EnumerateFiles(ordinary.Project, "*.tmp").Any(),
            "Cancellation changed the existing document or left an owned temporary.");
        using var immutableCancellation = new CancellationTokenSource();
        var immutable = Path.Combine(ordinary.Project, "object.json");
        RootedDirectory.TestObserver = (point, path) =>
        { if (point == "entry_opened" && path == immutable) immutableCancellation.Cancel(); };
        try
        {
            await CortexFileV2AtomicIo.WriteImmutableAsync(immutable, body, 128, ordinary.Project, immutableCancellation.Token).ConfigureAwait(false);
            throw new InvalidOperationException("Immutable cancellation was ignored.");
        }
        catch (OperationCanceledException) { }
        finally { RootedDirectory.TestObserver = old; }
        Require(!IoFile.Exists(immutable), "An interrupted immutable creation left a partial content-addressed object.");
        await WindowsReplacementMetadataAsync().ConfigureAwait(false);
    }

    private static async Task WindowsReplacementConflictAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var scope = new DirectorySwapFixture();
        var target = Path.Combine(scope.Project, "target-conflict.json");
        var retained = Path.Combine(scope.Project, "retained-target.json");
        IoFile.WriteAllText(target, "old");
        var previous = RootedDirectory.TestObserver;
        var attempted = false; var writeBlocked = false; var renameBlocked = false; var renamed = false;
        Exception? failure = null;
        RootedDirectory.TestObserver = (point, path) =>
        {
            if (attempted || point != "replacement_metadata_pinned" || path != target) return;
            attempted = true;
            try { IoFile.WriteAllText(target, "changed"); }
            catch (IOException error) when ((error.HResult & 0xffff) == 32) { writeBlocked = true; }
            try { IoFile.Move(target, retained); renamed = true; }
            catch (IOException error) when ((error.HResult & 0xffff) == 32) { renameBlocked = true; }
            if (renamed) IoFile.WriteAllText(target, "newcomer");
        };
        try { await CortexFileV2AtomicIo.WriteAtomicAsync(target, Encoding.UTF8.GetBytes("new"), 64, scope.Project, default).ConfigureAwait(false); }
        catch (IOException error) { failure = error; }
        finally { RootedDirectory.TestObserver = previous; }
        var diagnostic = Diagnostic(attempted, renamed, failure) + $"; writeBlocked={writeBlocked}; renameBlocked={renameBlocked}";
        Require(attempted && writeBlocked,
            "The pinned replacement target permitted an ordinary byte write. " + diagnostic);
        if (renamed)
            Require(failure is not null && IoFile.ReadAllText(target) == "newcomer" && IoFile.ReadAllText(retained) == "old",
                "Publication overwrote a newcomer or changed the renamed original target. " + diagnostic);
        else
            Require(renameBlocked && failure is null && IoFile.ReadAllText(target) == "new",
                "A blocked target rename prevented the original atomic publication. " + diagnostic);
        Require(!Directory.EnumerateFiles(scope.Project, "*.tmp").Any(), "A refused replacement left its owned temporary.");
    }

    private static async Task WindowsReplacementMetadataAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var scope = new DirectorySwapFixture();
        var target = Path.Combine(scope.Project, "metadata.json");
        IoFile.WriteAllText(target, "old");
        var creation = new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        IoFile.SetCreationTimeUtc(target, creation);
        IoFile.SetAttributes(target, FileAttributes.Hidden | FileAttributes.Archive);
        byte[]? expectedDacl = null;
        try
        {
            var access = new FileInfo(target).GetAccessControl(AccessControlSections.Access);
            access.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            new FileInfo(target).SetAccessControl(access);
            var applied = new FileInfo(target).GetAccessControl(AccessControlSections.Access);
            var descriptor = new RawSecurityDescriptor(applied.GetSecurityDescriptorBinaryForm(), 0);
            if (descriptor.DiscretionaryAcl is { } dacl)
            { expectedDacl = new byte[dacl.BinaryLength]; dacl.GetBinaryForm(expectedDacl, 0); }
        }
        catch (PlatformNotSupportedException)
        { Console.WriteLine("SKIP Windows DACL fixture: the scratch filesystem does not support persistent ACLs."); }
        await CortexFileV2AtomicIo.WriteAtomicAsync(target, Encoding.UTF8.GetBytes("new"), 128, scope.Project, default).ConfigureAwait(false);
        Require(IoFile.ReadAllText(target) == "new" && IoFile.GetCreationTimeUtc(target) == creation &&
            (IoFile.GetAttributes(target) & FileAttributes.Hidden) != 0, "Atomic replacement lost creation/visibility metadata.");
        if (expectedDacl is not null)
        {
            var access = new FileInfo(target).GetAccessControl(AccessControlSections.Access);
            var descriptor = new RawSecurityDescriptor(access.GetSecurityDescriptorBinaryForm(), 0);
            var dacl = descriptor.DiscretionaryAcl ?? throw new InvalidOperationException("Replacement lost its DACL.");
            var actual = new byte[dacl.BinaryLength]; dacl.GetBinaryForm(actual, 0);
            Require(access.AreAccessRulesProtected && expectedDacl.SequenceEqual(actual), "Atomic replacement weakened or changed the existing protected DACL.");
        }
    }

    private static string Diagnostic(bool attempted, bool swapped, Exception? error)
    {
        IEnumerable<Exception> causes = error is AggregateException aggregate ? aggregate.InnerExceptions.Take(4)
            : error is null ? [] : new[] { error };
        return $"attempted={attempted}; swapped={swapped}; causes=" + string.Join(",", causes.Select(
            cause => cause.GetType().Name + "/0x" + cause.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) +
                "/" + (cause.Data["RootedIoStage"] as string ?? "unspecified")));
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
