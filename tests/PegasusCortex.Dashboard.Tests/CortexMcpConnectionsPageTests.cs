using Pegasus.AgentOps.Tray;
using Pegasus.Cortex.Product;

internal static class CortexMcpConnectionsPageTests
{
    internal static int Run()
    {
        try
        {
            UnobservedAndOrdinary();
            FullControlAndRevocation();
            PendingRequestAndNoReplay();
            UnknownAndReadFailure();
            DisposalAndBusy();
            ClosedHelperArgumentsAndPins();
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL MCP connections page: " + error); return 1; }
        Console.WriteLine("PASS MCP connections page/control (injected registry, public drafts, clipboard and confirmations; no vault, UAC, machine store or runtime actions)");
        return 0;
    }

    private static void UnobservedAndOrdinary()
    {
        var controller = new FakeController { Pending = [Request()] };
        string? copied = null;
        using var page = Page(controller, value => copied = value);
        Check(controller.Reads == 0 && !Find<Button>(page, "Approve ordinary access").Enabled,
            "construction read state or enabled an unobserved approval");
        page.Reload();
        Check(controller.Reads == 1 && Find<Button>(page, "Approve ordinary access").Enabled,
            "explicit refresh did not expose a pending request");
        var warning = Find<Label>(page, "Full control warning");
        Check(warning.Font.Bold && warning.BackColor.R > warning.BackColor.G &&
            warning.Text.Contains("Every chat sharing the credential", StringComparison.Ordinal) &&
            warning.Text.Contains("does not isolate", StringComparison.Ordinal),
            "shared-credential danger warning is missing");
        Check(Find<DataGridView>(page, "MCP connections").ReadOnly, "registry grid is editable");
        Find<Button>(page, "Copy full connection ID").PerformClick();
        Check(copied == Request().ConnectionId, "clipboard copied something other than the complete public ID");
        page.ApproveAsync(false).GetAwaiter().GetResult();
        Check(controller.Approvals == 1 && controller.LastApproval is { Revision: 0, Full: false } &&
            controller.Store!.Connections.Single() is { Enabled: true, FullControl: false, Revision: 1 },
            "ordinary approval did not use the observed revision/request");
        Check(!Find<Button>(page, "Approve ordinary access").Enabled &&
            Find<Button>(page, "Grant Full control…").Enabled &&
            Find<Label>(page, "Connection approval detail").Text.Contains("revision 1", StringComparison.Ordinal),
            "current ordinary approval state was not re-read");
        Find<Button>(page, "Copy Codex settings").PerformClick();
        Check(copied == "public-settings:" + Request().ConnectionId,
            "client setup was not copied from the selected approved connection");
    }

    private static void FullControlAndRevocation()
    {
        var controller = new FakeController { Store = Store(new CortexMcpConnectionGrant(Request(), new string('e', 32), 1, true, false)) };
        var allowFull = false; var fullQuestions = 0; var allowRevoke = false;
        using var page = Page(controller, confirmFull: _ => { fullQuestions++; return allowFull; },
            confirmRevoke: _ => allowRevoke);
        page.Reload();
        page.ApproveAsync(true).GetAwaiter().GetResult();
        Check(fullQuestions == 1 && controller.Approvals == 0, "cancelled Full control reached the controller");
        allowFull = true;
        page.ApproveAsync(true).GetAwaiter().GetResult();
        Check(controller.LastApproval is { Revision: 1, Full: true } &&
            controller.Store!.Connections.Single() is { FullControl: true, Revision: 2 },
            "Full control was not separately approved against the actual revision");
        page.RevokeAsync(true).GetAwaiter().GetResult();
        Check(controller.LastRevocation is { Revision: 2, FullOnly: true } &&
            controller.Store!.Connections.Single() is { Enabled: true, FullControl: false, Revision: 3 },
            "revoking Full control also revoked ordinary access");
        page.RevokeAsync(false).GetAwaiter().GetResult();
        Check(controller.Revocations == 1, "cancelled revoke-all reached the controller");
        allowRevoke = true;
        page.RevokeAsync(false).GetAwaiter().GetResult();
        Check(controller.LastRevocation is { Revision: 3, FullOnly: false } &&
            controller.Store!.Connections.Single() is { Enabled: false, FullControl: false, Revision: 4 },
            "revoke-all did not preserve the actual revision transition");
    }

    private static void PendingRequestAndNoReplay()
    {
        var controller = new FakeController();
        using var page = Page(controller);
        page.Reload();
        Find<TextBox>(page, "Connection name").Text = "Local research";
        Find<TextBox>(page, "Client application").Text = "Codex";
        Find<TextBox>(page, "Optional chat label").Text = "Project discussion";
        page.CreateLocal();
        var request = controller.Created ?? throw new Exception("no public local draft");
        Check(controller.Creates == 1 && controller.Approvals == 0 &&
            Find<TextBox>(page, "Full connection ID").Text == request.ConnectionId,
            "creating a local draft approved it or lost its ID");
        controller.Result = new(false, "Windows approval cancelled.");
        var reads = controller.Reads;
        page.ApproveAsync(false).GetAwaiter().GetResult();
        page.ApproveAsync(false).GetAwaiter().GetResult();
        Check(controller.Approvals == 1 && controller.Reads == reads && controller.Creates == 1,
            "an unconfirmed approval was refreshed or replayed automatically");
        page.Reload();
        controller.Result = new(true);
        page.ApproveAsync(false).GetAwaiter().GetResult();
        Check(controller.Approvals == 2 && controller.Creates == 1 &&
            controller.LastApproval?.Request == request && controller.LastApproval?.Revision == 0,
            "explicit later approval regenerated the credential or changed the public request");
    }

    private static void UnknownAndReadFailure()
    {
        var controller = new FakeController { Pending = [Request()], Persist = false };
        using var page = Page(controller);
        page.Reload();
        page.ApproveAsync(false).GetAwaiter().GetResult();
        Check(!Find<Button>(page, "Approve ordinary access").Enabled &&
            Find<Label>(page, "Connection status").Text.Contains("not confirmed", StringComparison.Ordinal),
            "helper success fabricated registry approval");
        page.ApproveAsync(false).GetAwaiter().GetResult();
        Check(controller.Approvals == 1, "unknown outcome was replayed");
        controller.ThrowRead = true;
        page.Reload();
        Check(Find<DataGridView>(page, "MCP connections").Rows.Count == 0 &&
            !Find<Button>(page, "Grant Full control…").Enabled &&
            Find<Label>(page, "Connection status").Text.Contains("could not be read", StringComparison.Ordinal),
            "read failure selected permissive defaults");

        var conflict = new FakeController
        {
            Store = Store(new CortexMcpConnectionGrant(Request(), new string('e', 32), 1, true, false)),
            Pending = [Request() with { CredentialSha256 = new string('c', 64) }]
        };
        using var conflictPage = Page(conflict);
        conflictPage.Reload();
        Check(!Find<Button>(conflictPage, "Grant Full control…").Enabled &&
            Find<Button>(conflictPage, "Revoke all access…").Enabled,
            "conflicting enrollment became approvable or prevented actual grant revocation");

        var imported = new FakeController();
        using var importPage = Page(imported, chooseImport: () => "fixture-public-request.json");
        importPage.Reload();
        importPage.ImportPublic();
        Check(imported.Imports == 1 && imported.Creates == 0 && imported.Approvals == 0,
            "public import created a credential or granted access");
    }

    private static void DisposalAndBusy()
    {
        var completion = new TaskCompletionSource<CortexMcpConnectionControlResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new FakeController { Pending = [Request()], PendingResult = completion.Task };
        var page = Page(controller);
        page.Reload();
        var context = SynchronizationContext.Current;
        Task pending;
        // Observe disposal without a WinForms message loop or real helper.
        try { SynchronizationContext.SetSynchronizationContext(null); pending = page.ApproveAsync(false); }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
        page.ApproveAsync(false).GetAwaiter().GetResult();
        Check(controller.Approvals == 1 && !Find<Button>(page, "Refresh").Enabled,
            "busy page allowed a second action");
        var reads = controller.Reads;
        page.Dispose();
        page.Dispose();
        completion.SetResult(new(true));
        pending.GetAwaiter().GetResult();
        Check(controller.Reads == reads && controller.Approvals == 1,
            "disposed continuation reloaded or replayed an action");
    }

    private static void ClosedHelperArgumentsAndPins()
    {
        var id = Request().ConnectionId;
        var sha = new string('c', 64);
        const string owner = "S-1-5-21-1-2-3-1001";
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "public-enrollment.json"));
        var approve = CortexMcpConnectionsControl.ParseArguments("approve", path, sha, id, "0", "full", owner);
        var arguments = CortexMcpConnectionsControl.Arguments(approve);
        Check(arguments.Length == 8 && arguments[0] == "--mcp-connection-control" &&
            arguments[2] == path && arguments[4] == id, "fixed helper vector lost its independent ID");
        Check(CortexMcpConnectionsControl.RequirePinnedRequest(approve, Request(), sha) == Request(),
            "matching public request pin was refused");
        Reject(() => CortexMcpConnectionsControl.RequirePinnedRequest(approve, Request('b'), sha));
        Reject(() => CortexMcpConnectionsControl.RequirePinnedRequest(approve, Request(), new string('d', 64)));
        Reject(() => CortexMcpConnectionsControl.ParseArguments("approve", path, "-", id, "0", "full", owner));
        Reject(() => CortexMcpConnectionsControl.ParseArguments("approve", path, sha, id, "+1", "full", owner));
        Reject(() => CortexMcpConnectionsControl.ParseArguments("approve", path, sha, id, "0", "FULL", owner));
        Reject(() => CortexMcpConnectionsControl.ParseArguments("approve", path, sha, id, "0", "full", "S-1-5-18"));
        Reject(() => CortexMcpConnectionsControl.ParseArguments("revoke", Request('b').ConnectionId, "-", id, "1", "ordinary", owner));
        Reject(() => CortexMcpConnectionsControl.ParseArguments("revoke-full", id, "-", id, "1", "ordinary", owner));
        _ = CortexMcpConnectionsControl.ParseArguments("revoke-full", id, "-", id, "1", "full", owner);
    }

    private static CortexMcpConnectionsPage Page(FakeController controller, Action<string>? clipboard = null,
        Func<CortexMcpConnectionRequest, bool>? confirmFull = null,
        Func<CortexMcpConnectionRequest, bool>? confirmRevoke = null, Func<string?>? chooseImport = null) =>
        new(controller, clipboard ?? (_ => { }), chooseImport ?? (() => null), confirmFull ?? (_ => true),
            confirmRevoke ?? (_ => true), _ => { }, autoLoad:false,
            codexConfiguration:id => "public-settings:" + id);
    private static CortexMcpConnectionRequest Request(char id = 'a') =>
        new(CortexMcpConnections.RequestSchema, new string(id, 32), new string('b', 64),
            "Research client", "Codex", "Shared discussion");
    private static CortexMcpConnectionStore Store(params CortexMcpConnectionGrant[] grants) =>
        new(CortexMcpConnections.StoreSchema, new string('f', 32), 1, grants);
    private static T Find<T>(Control parent, string name) where T : Control =>
        Descendants(parent).OfType<T>().Single(control => control.AccessibleName == name);
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException or ArgumentException) { return; }
        throw new Exception("Invalid helper selection was admitted.");
    }

    private sealed class FakeController : ICortexMcpConnectionsController
    {
        internal CortexMcpConnectionStore? Store;
        internal CortexMcpConnectionRequest[] Pending = [];
        internal CortexMcpConnectionRequest? Created;
        internal int Reads, Creates, Imports, Approvals, Revocations;
        internal bool Persist = true, ThrowRead;
        internal CortexMcpConnectionControlResult Result = new(true);
        internal Task<CortexMcpConnectionControlResult>? PendingResult;
        internal (CortexMcpConnectionRequest Request, long Revision, bool Full)? LastApproval;
        internal (string Id, long Revision, bool FullOnly)? LastRevocation;
        public CortexMcpConnectionsSnapshot Read()
        {
            Reads++;
            if (ThrowRead) throw new InvalidDataException("fixture");
            return new(Store, Pending);
        }
        public CortexMcpConnectionDraft Create(string name, string application, string? chatLabel)
        {
            Creates++;
            Created = Request('c') with { Name = name, Application = application, ChatLabel = chatLabel };
            Pending = [Created];
            return new(Created);
        }
        public CortexMcpConnectionDraft Import(string path) { Imports++; Pending = [Request()]; return new(Request()); }
        public Task<CortexMcpConnectionControlResult> ApproveAsync(CortexMcpConnectionRequest request,
            long expectedRevision, bool fullControl, CancellationToken cancellationToken)
        {
            Approvals++; LastApproval = (request, expectedRevision, fullControl);
            if (PendingResult is not null) return PendingResult;
            if (Result.Confirmed && Persist)
            {
                var prior = Store?.Connections ?? [];
                var grant = new CortexMcpConnectionGrant(request, new string('e', 32), expectedRevision + 1, true, fullControl);
                Store = new(CortexMcpConnections.StoreSchema, new string('f', 32), (Store?.Revision ?? 0) + 1,
                    prior.Where(item => item.Request.ConnectionId != request.ConnectionId).Append(grant).ToArray());
            }
            return Task.FromResult(Result);
        }
        public Task<CortexMcpConnectionControlResult> RevokeAsync(string id, long expectedRevision,
            bool fullControlOnly, CancellationToken cancellationToken)
        {
            Revocations++; LastRevocation = (id, expectedRevision, fullControlOnly);
            if (Result.Confirmed && Persist)
            {
                var current = Store ?? throw new InvalidOperationException("Missing fixture grant.");
                Store = current with { Revision = current.Revision + 1, Connections = current.Connections.Select(item =>
                    item.Request.ConnectionId != id ? item : item with { Revision = expectedRevision + 1,
                        FullControl = false, Enabled = fullControlOnly && item.Enabled }).ToArray() };
            }
            return Task.FromResult(Result);
        }
    }
}
