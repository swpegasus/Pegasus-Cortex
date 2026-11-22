using Pegasus.AgentOps.Engine;
using Pegasus.AgentOps.Tray;

internal static class CortexCertificatePasswordsPageTests
{
    private static string? _assertion;
    internal static int Run()
    {
        _assertion = null;
        try
        {
            EntryAndSave();
            UnknownAndFailure();
            RemovalAndOwnership();
            NoHostAndDisposal();
            Console.WriteLine("PASS certificate-password page: synthetic masked native textbox, injected credential controller/owner leases, save/clear, uncertain-result no-retry, exact confirmed removal and disposal; no Windows credential read/write");
            return 0;
        }
        catch (Exception error)
        { Console.Error.WriteLine("FAIL certificate-password page fixture: " + (_assertion ?? error.GetType().Name)); return 1; }
    }

    private static void EntryAndSave()
    {
        var owner = new Owner();
        var controller = new Fake(owner);
        using var page = Page(owner, controller);
        var reference = Find<TextBox>(page, "Certificate password reference");
        var password = Find<TextBox>(page, "Certificate password value");
        Check(controller.Calls == 0 && owner.Starts == 0 && password.UseSystemPasswordChar && password.MaxLength == 1024,
            "construction accessed credentials or lost masked bounded input");
        reference.Text = "archive-password";
        page.SavePassword();
        Check(controller.Saves == 0 && owner.Active == 0 && Find<Button>(page, "Save or replace certificate password").Enabled,
            "empty password reached store or invented an unconfirmed effect");
        password.Text = "synthetic-p\u00e4\U0001f600";
        controller.Expected = password.Text; // Synthetic fixture value only; product never reads Text.
        page.SavePassword();
        Check(controller.Saves == 1 && controller.LastReference == "archive-password" && password.TextLength == 0 && owner.Active == 0,
            "exact Unicode entry, owned save or clearing failed");
        Check(Status(page).Contains("saved and read back", StringComparison.Ordinal), "save confirmation missing");
        password.Text = "synthetic-discarded";
        reference.Text = "next-password";
        Check(password.TextLength == 0 && controller.Saves == 1, "reference switch retained password or wrote it");
        page.CheckReference();
        Check(controller.Checks == 1 && Status(page).Contains("not been revealed or verified", StringComparison.Ordinal),
            "presence was presented as password verification");
    }

    private static void UnknownAndFailure()
    {
        var owner = new Owner();
        var controller = new Fake(owner) { SaveResult = new(false, "unknown", "not_displayed", true, true) };
        using var page = Page(owner, controller);
        Find<TextBox>(page, "Certificate password reference").Text = "archive-password";
        var password = Find<TextBox>(page, "Certificate password value");
        password.Text = "synthetic";
        controller.Expected = "synthetic";
        page.SavePassword();
        page.SavePassword();
        page.RemovePassword();
        Check(controller.Saves == 1 && controller.Removes == 0 && password.TextLength == 0 &&
            !Find<Button>(page, "Save or replace certificate password").Enabled &&
            !Find<Button>(page, "Remove certificate password").Enabled,
            "unconfirmed mutation retried or retained password");
        page.CheckReference();
        Check(controller.Checks == 1 && Find<Button>(page, "Save or replace certificate password").Enabled &&
            Status(page).Contains("earlier change remains unconfirmed", StringComparison.Ordinal),
            "explicit presence check falsely resolved prior mutation or did not permit a deliberate new action");
        controller.SaveResult = new(false, "failed", "synthetic-private-marker");
        password.Text = "synthetic";
        page.SavePassword();
        Check(!Status(page).Contains("synthetic-private-marker", StringComparison.Ordinal) && password.TextLength == 0,
            "arbitrary backend text or entered password reached visible status");
        owner.Refuse = true;
        password.Text = "synthetic";
        var before = controller.Calls;
        page.SavePassword();
        Check(controller.Calls == before && owner.Active == 0 && password.TextLength == 0 &&
            Status(page).Contains("could not start", StringComparison.Ordinal), "owner refusal dispatched or invented a write");
    }

    private static void RemovalAndOwnership()
    {
        var owner = new Owner();
        var controller = new Fake(owner);
        var confirmation = false;
        using var page = new CortexCertificatePasswordsPage(owner.Enter, controller, () => confirmation, _ => { });
        var reference = Find<TextBox>(page, "Certificate password reference");
        reference.Text = "archive-password";
        page.RemovePassword();
        Check(controller.Removes == 0 && owner.Starts == 0, "cancelled confirmation reached native controller");
        confirmation = true;
        page.RemovePassword();
        Check(controller.Removes == 1 && controller.LastReference == "archive-password" && owner.Active == 0,
            "confirmed exact removal missed owner lifetime");
        CortexCertificatePasswordsPage? changed = null;
        using (changed = new CortexCertificatePasswordsPage(owner.Enter, controller, () =>
        {
            Find<TextBox>(changed!, "Certificate password reference").Text = "different-password";
            return true;
        }, _ => { }))
        {
            Find<TextBox>(changed, "Certificate password reference").Text = "archive-password";
            changed.RemovePassword();
        }
        Check(controller.Removes == 1, "confirmation for one reference removed another reference");
    }

    private static void NoHostAndDisposal()
    {
        var owner = new Owner();
        var controller = new Fake(owner);
        using (var page = new CortexCertificatePasswordsPage(controller: controller, theme: _ => { }))
        {
            Find<TextBox>(page, "Certificate password reference").Text = "archive-password";
            page.CheckReference(); page.SavePassword(); page.RemovePassword();
            Check(controller.Calls == 0 && !Find<Button>(page, "Check password reference").Enabled,
                "standalone dashboard became a credential writer/reader");
        }
        var disposed = Page(owner, controller);
        var password = Find<TextBox>(disposed, "Certificate password value");
        password.Text = "synthetic-disposed";
        disposed.Dispose();
        disposed.Dispose();
        disposed.CheckReference(); disposed.SavePassword(); disposed.RemovePassword();
        Check(controller.Calls == 0 && owner.Active == 0, "disposed controls started an operation");
    }

    private static CortexCertificatePasswordsPage Page(Owner owner, Fake controller) => new(owner.Enter, controller, () => true, _ => { });
    private static string Status(Control root) => Find<Label>(root, "Certificate password status").Text;
    private static T Find<T>(Control root, string name) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T found && child.AccessibleName == name) return found;
            if (FindOrNull<T>(child, name) is { } nested) return nested;
        }
        throw new InvalidOperationException("fixture_control_missing");
    }
    private static T? FindOrNull<T>(Control root, string name) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T found && child.AccessibleName == name) return found;
            if (FindOrNull<T>(child, name) is { } nested) return nested;
        }
        return null;
    }
    private sealed class Owner
    {
        internal int Starts, Active;
        internal bool Refuse;
        internal IDisposable Enter()
        {
            if (Refuse) throw new InvalidOperationException("host_stopping");
            Starts++; Active++;
            return new Lease(() => Active--);
        }
    }
    private sealed class Lease(Action end) : IDisposable
    {
        private Action? _end = end;
        public void Dispose() => Interlocked.Exchange(ref _end, null)?.Invoke();
    }
    private sealed class Fake(Owner owner) : ICortexCertificatePasswordController
    {
        internal int Saves, Checks, Removes;
        internal int Calls => Saves + Checks + Removes;
        internal string? LastReference;
        internal string? Expected;
        internal OwnerCertificateSecretResult SaveResult = new(true, "saved", ActionIssued: true);
        public OwnerCertificateSecretResult Inspect(string reference)
        { Check(owner.Active == 1, "check without owner lifetime"); Checks++; LastReference = reference; return new(true, "present"); }
        public OwnerCertificateSecretResult Save(string reference, ReadOnlySpan<char> password)
        {
            Check(owner.Active == 1 && password.SequenceEqual(Expected), "save without owner or exact synthetic password");
            Saves++; LastReference = reference; return SaveResult;
        }
        public OwnerCertificateSecretResult Remove(string reference)
        { Check(owner.Active == 1, "remove without owner lifetime"); Removes++; LastReference = reference; return new(true, "removed", ActionIssued: true); }
    }
    private static void Check(bool value, string code)
    { if (!value) { _assertion = code; throw new InvalidOperationException("password_page_assertion_failed"); } }
}
