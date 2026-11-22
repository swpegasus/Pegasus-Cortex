using System.Runtime.InteropServices;
using Pegasus.AgentOps.Engine;

namespace Pegasus.AgentOps.Tray;

internal interface ICortexCertificatePasswordController
{
    OwnerCertificateSecretResult Inspect(string reference);
    OwnerCertificateSecretResult Save(string reference, ReadOnlySpan<char> password);
    OwnerCertificateSecretResult Remove(string reference);
}

internal sealed class CortexCertificatePasswordController(string ownerSid) : ICortexCertificatePasswordController
{
    private readonly OwnerCertificateSecretStore _store = new(ownerSid);
    public OwnerCertificateSecretResult Inspect(string reference) => _store.Inspect(reference);
    public OwnerCertificateSecretResult Save(string reference, ReadOnlySpan<char> password) => _store.Save(reference, password);
    public OwnerCertificateSecretResult Remove(string reference) => _store.Remove(reference);
}

internal sealed class CortexSmbPasswordController(string ownerSid) : ICortexCertificatePasswordController
{
    private readonly OwnerSmbPasswordReferences _store = new(ownerSid);
    public OwnerCertificateSecretResult Inspect(string reference) => _store.Inspect(reference);
    public OwnerCertificateSecretResult Save(string reference, ReadOnlySpan<char> password) => _store.Save(reference, password);
    public OwnerCertificateSecretResult Remove(string reference) => _store.Remove(reference);
}

/// <summary>Owner-local entry only; passwords never become operation arguments or receipt bodies.</summary>
internal sealed class CortexCertificatePasswordsPage : UserControl
{
    private readonly ICortexCertificatePasswordController _controller;
    private readonly Func<IDisposable>? _beginOperation;
    private readonly Func<bool> _confirmRemove;
    private readonly TextBox _reference = new() { Width = 350, MaxLength = 64, AccessibleName = "Certificate password reference" };
    private readonly TextBox _password = new()
    {
        Width = 350, MaxLength = 1024, UseSystemPasswordChar = true,
        AccessibleName = "Certificate password value", AutoCompleteMode = AutoCompleteMode.None
    };
    private readonly Button _check = new() { AutoSize = true, Text = "Check reference", AccessibleName = "Check password reference" };
    private readonly Button _save = new() { AutoSize = true, Text = "Save or replace password", AccessibleName = "Save or replace certificate password" };
    private readonly Button _remove = new() { AutoSize = true, Text = "Remove password…", AccessibleName = "Remove certificate password" };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(680, 0), AccessibleName = "Certificate password status" };
    private bool _busy;
    private bool _clearFailed;
    private string? _unconfirmedReference;

    internal CortexCertificatePasswordsPage(Func<IDisposable>? beginOperation = null,
        ICortexCertificatePasswordController? controller = null, Func<bool>? confirmRemove = null,
        Action<Control>? theme = null, OwnerPasswordPurpose purpose = OwnerPasswordPurpose.CertificatePassword)
    {
        if (purpose is not (OwnerPasswordPurpose.CertificatePassword or OwnerPasswordPurpose.SmbPassword))
            throw new ArgumentOutOfRangeException(nameof(purpose));
        var smb = purpose == OwnerPasswordPurpose.SmbPassword;
        var purposeName = smb ? "SMB password" : "Certificate password";
        _reference.AccessibleName = purposeName + " reference";
        _password.AccessibleName = purposeName + " value";
        _save.AccessibleName = "Save or replace " + purposeName.ToLowerInvariant();
        _remove.AccessibleName = "Remove " + purposeName.ToLowerInvariant();
        _status.AccessibleName = purposeName + " status";
        _beginOperation = beginOperation;
        _controller = controller ?? (smb
            ? (ICortexCertificatePasswordController)new CortexSmbPasswordController(LocalIdentity.Current().Sid)
            : new CortexCertificatePasswordController(LocalIdentity.Current().Sid));
        _confirmRemove = confirmRemove ?? (() => MessageBox.Show(this,
            smb ? "Remove the stored password for this reference? SMB connections using it will require a new password."
                : "Remove the stored password for this reference? Certificate imports or exports using it will require a new password.",
            "Remove " + purposeName.ToLowerInvariant(), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes);
        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(20)
        };
        body.Controls.Add(new Label { AutoSize = true, Font = CortexTheme.HeadingFont, Text = smb ? "SMB passwords" : "Certificate passwords" });
        body.Controls.Add(Description((smb
            ? "Save an SMB password in your Windows credential store, then use its reference in a connection profile. "
            : "Save a password in your Windows credential store, then use its reference for protected certificate import or export. ") +
            "Passwords stay with your Windows user on this computer and are not sent through MCP or stored in Cortex jobs."));
        body.Controls.Add(Description("Reference (lowercase letters, numbers, dots, underscores or hyphens)"));
        body.Controls.Add(_reference);
        body.Controls.Add(Description("Password (enter a value only when saving or replacing it)"));
        body.Controls.Add(_password);
        body.Controls.Add(Description("Saving replaces any password already stored under this exact reference. Checking reports presence only; it does not reveal or verify the password."));
        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.AddRange([_check, _save, _remove]);
        body.Controls.Add(buttons);
        body.Controls.Add(_status);
        Controls.Add(body);
        _reference.TextChanged += (_, _) =>
        {
            ClearPassword();
            _status.Text = _unconfirmedReference is null ? "This reference has not been checked." : UnconfirmedText;
            UpdateControls();
        };
        _check.Click += (_, _) => CheckReference();
        _save.Click += (_, _) => SavePassword();
        _remove.Click += (_, _) => RemovePassword();
        _status.Text = beginOperation is null
            ? "Password management requires the ordinary running Cortex host."
            : "Enter a reference. No passwords have been read.";
        UpdateControls();
        (theme ?? CortexTheme.Apply)(this);
    }

    private const string UnconfirmedText = "An earlier change was not confirmed. Check that reference before another change; presence alone does not verify the earlier password value.";
    private static Label Description(string text) => new() { AutoSize = true, MaximumSize = new Size(680, 0), Text = text };

    internal void CheckReference() => Run(reference =>
    {
        var result = _controller.Inspect(reference);
        if (result.Ok && result.State is "present" or "missing")
        {
            var wasUnconfirmed = _unconfirmedReference == reference;
            if (wasUnconfirmed) _unconfirmedReference = null;
            _status.Text = result.State == "present" ? "A password reference is present. Its password has not been revealed or verified." : "No password is stored under this reference.";
            if (wasUnconfirmed) _status.Text += " The earlier change remains unconfirmed. A new change must be deliberate.";
        }
        else ShowFailure(result, reference);
    });

    internal void SavePassword() => Run(reference =>
    {
        char[]? buffer = null;
        try
        {
            (buffer, var length) = ReadPassword(_password);
            var result = _controller.Save(reference, buffer.AsSpan(0, length));
            if (result.Ok && result.State == "saved") _status.Text = "Password saved and read back for this reference.";
            else ShowFailure(result, reference);
        }
        catch (PasswordInputException)
        { _status.Text = "Enter a password of 1–1024 characters before saving."; }
        finally
        {
            if (buffer is not null) Array.Clear(buffer);
            ClearPassword();
        }
    }, mutation: true);

    internal void RemovePassword()
    {
        if (!CanRun(mutation: true)) return;
        ClearPassword();
        var selected = _reference.Text;
        var confirmed = false;
        _busy = true;
        UpdateControls();
        try { confirmed = _confirmRemove(); }
        finally { _busy = false; UpdateControls(); }
        if (!confirmed || IsDisposed || Disposing || selected != _reference.Text) return;
        Run(reference =>
        {
            var result = _controller.Remove(reference);
            if (result.Ok && result.State is "removed" or "absent") _status.Text = "No password is stored under this reference.";
            else ShowFailure(result, reference);
        }, mutation: true);
    }

    private bool CanRun(bool mutation) => !_busy && !IsDisposed && !Disposing &&
        _beginOperation is not null && _reference.Text.Length > 0 && (!mutation || (!_clearFailed && _unconfirmedReference is null));

    private void Run(Action<string> action, bool mutation = false)
    {
        if (!CanRun(mutation)) return;
        var reference = _reference.Text;
        var entered = false;
        _busy = true;
        UpdateControls();
        try
        {
            using var ownership = _beginOperation!();
            entered = true;
            action(reference);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            // The store reports post-entry uncertainty itself. An unexpected UI
            // failure cannot prove a mutation did not occur or permit replay.
            if (mutation && entered) _unconfirmedReference = reference;
            _status.Text = mutation && entered ? UnconfirmedText : "The operation could not start. Use the ordinary Cortex host and resolve any maintenance or Windows credential-store error.";
        }
        finally
        {
            ClearPassword();
            _busy = false;
            UpdateControls();
        }
    }

    private void ShowFailure(OwnerCertificateSecretResult result, string reference)
    {
        if (result.OutcomeUnknown || result.ActionIssued)
        {
            _unconfirmedReference = reference;
            _status.Text = UnconfirmedText;
        }
        else _status.Text = result.Error switch
        {
            "certificate_secret_reference_invalid" or "smb_password_reference_invalid" => "Use a reference of 1–64 lowercase letters, numbers, dots, underscores or hyphens, starting with a letter or number.",
            "certificate_secret_owner_denied" or "smb_owner_denied" => "Use the ordinary signed-in Cortex session for this Windows user.",
            "certificate_secret_blob_invalid" or "smb_password_blob_invalid" => "Enter a valid password of 1–1024 characters without a zero character.",
            _ => "Windows did not confirm this operation. No automatic retry was made."
        };
    }

    private void UpdateControls()
    {
        if (IsDisposed || Disposing) return;
        _reference.Enabled = !_busy && _beginOperation is not null;
        _password.Enabled = _reference.Enabled && !_clearFailed;
        _check.Enabled = CanRun(mutation: false);
        _save.Enabled = _remove.Enabled = CanRun(mutation: true);
        if (_clearFailed) _status.Text = "The password field could not be cleared. Close this page before another change.";
    }

    private static (char[] Buffer, int Length) ReadPassword(TextBox control)
    {
        // Read into a mutable owned buffer, never Text, a string argument, a
        // clipboard value or a durable operation. The UI thread owns this call.
        var window = control.Handle;
        var length = GetWindowTextLengthW(window);
        if (length is < 1 or > 1024) throw new PasswordInputException();
        var buffer = new char[length + 1];
        try
        {
            var read = GetWindowTextW(window, buffer, buffer.Length);
            if (read != length || GetWindowTextLengthW(window) != length)
                throw new PasswordInputException();
            return (buffer, read);
        }
        catch { Array.Clear(buffer); throw; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ClearPassword();
        base.Dispose(disposing);
    }

    private void ClearPassword()
    {
        if (_password.IsDisposed || !_password.IsHandleCreated) return;
        // WinForms Clear/Text setters first read the old native value into an
        // immutable string. Clear the existing native field directly instead.
        var window = _password.Handle;
        var cleared = SetWindowTextW(window, string.Empty);
        _ = SendMessageW(window, 0x00CD /* EM_EMPTYUNDOBUFFER */, 0, 0);
        _clearFailed = !cleared || GetWindowTextLengthW(window) != 0;
        if (_clearFailed && !IsDisposed && !Disposing)
            _status.Text = "The password field could not be cleared. Close this page before another change.";
    }

    private sealed class PasswordInputException : Exception { }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetWindowTextLengthW(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetWindowTextW(nint window, [Out] char[] buffer, int maximumCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowTextW(nint window, string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SendMessageW(nint window, uint message, nint wParam, nint lParam);
}
