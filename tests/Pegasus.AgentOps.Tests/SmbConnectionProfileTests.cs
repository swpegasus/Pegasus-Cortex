using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Pegasus.AgentOps.Engine;
using Pegasus.Cortex.Product;

internal static class SmbConnectionProfileTests
{
    internal static Task<int> RunAsync() => Task.FromResult(!OperatingSystem.IsWindows() ? 77 : RunWindows());

    [SupportedOSPlatform("windows")]
    private static int RunWindows()
    {
        var root = Path.Combine(Path.GetTempPath(), "cortex-smbp-" + Guid.NewGuid().ToString("N"));
        var assertions = 0;
        var ok = false;
        try
        {
            using var windows = WindowsIdentity.GetCurrent();
            var sid = windows.User!.Value;
            var peer = new LocalPeer(sid, 7);
            var current = peer;
            var profilesRoot = Path.Combine(root, "profiles");
            var store = new OwnerSmbConnectionProfiles(profilesRoot, sid, () => current);
            var profile = new SmbConnectionProfile(1, "demo", "Build share", @"\\server\share", "X:", "currentUser");
            Check(store.Read("demo") is { Ok: true, State: "missing", Revision: 0 } &&
                store.List() is { Ok: true, NextId: null } && !Directory.Exists(root),
                "Passive reads initialized missing state.");
            Check(SmbConnectionProfile.Parse(JsonSerializer.SerializeToElement(profile, SmbConnectionProfile.Json)) == profile,
                "Closed profile roundtrip changed admitted fields.");
            foreach (var bad in new[]
            {
                profile with { Version = 2 }, profile with { Id = "../bad" }, profile with { Label = "line\nline" },
                profile with { RemoteShare = @"\\server\share\child" }, profile with { RemoteShare = @"\\?\C:" },
                profile with { RemoteShare = @"\\server@SSL\share" }, profile with { RemoteShare = @"\\server\.." },
                profile with { RemoteShare = @"\\server\share." }, profile with { LocalDrive = "x:" },
                profile with { LocalDrive = "X:\\" }, profile with { IdentityMode = "system" },
                profile with { RemoteUser = "user" }, profile with { PasswordReference = "ref" },
                profile with { IdentityMode = "explicitRemote", RemoteUser = "user" },
                profile with { IdentityMode = "explicitRemote", PasswordReference = "ref" },
                profile with { IdentityMode = "explicitRemote", RemoteUser = "user", PasswordReference = "UPPER" }
            }) ExpectInvalid(() => SmbConnectionProfile.Validate(bad));
            var encoded = JsonSerializer.Serialize(profile, SmbConnectionProfile.Json);
            foreach (var bad in new[]
            {
                encoded[..^1] + ",\"password\":\"not-admitted\"}",
                encoded[..^1] + ",\"remoteUser\":null}",
                encoded[..^1] + ",\"passwordReference\":null}",
                encoded[..^1] + ",\"version\":1}",
                encoded.Replace("\"kind\":\"smb-drive\"", "\"kind\":\"webdav\"", StringComparison.Ordinal),
                encoded.Replace("\"version\":1", "\"version\":\"1\"", StringComparison.Ordinal)
            })
            {
                using var parsed = JsonDocument.Parse(bad);
                ExpectInvalid(() => SmbConnectionProfile.Parse(parsed.RootElement));
            }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Check(store.Save(profile, 0, cancelled.Token) is { Ok: false, ActionIssued: false, OutcomeUnknown: false } &&
                !Directory.Exists(root), "Pre-entry cancellation initialized or published state.");
            foreach (var denied in new[] { peer with { Elevated = true }, peer with { Session = 0 },
                peer with { Session = 8 }, peer with { Sid = "S-1-5-21-100-200-300-9999" } })
            {
                current = denied;
                Check(!store.Read("demo").Ok && !store.Save(profile, 0).Ok && !Directory.Exists(root),
                    "A changed owner initialized profile state.");
            }
            current = peer;
            var saved = store.Save(profile, 0);
            Check(saved is { Ok: true, State: "saved", Revision: 1, ActionIssued: true, OutcomeUnknown: false } &&
                saved.Snapshot!.Profile == profile, "First profile was not published/read back.");
            var path = Path.Combine(profilesRoot, "smb-demo.v1.json");
            var original = File.ReadAllBytes(path);
            var acl = Descriptor(path);
            Check(saved.Snapshot!.ContentHash == Convert.ToHexStringLower(SHA256.HashData(original)), "Content hash was not the actual document hash.");
            Check(store.Save(profile with { Label = "Rejected" }, 0) is { Ok: false, ActionIssued: false, Error: "smb_profile_revision_conflict" } &&
                original.AsSpan().SequenceEqual(File.ReadAllBytes(path)) && Descriptor(path) == acl,
                "Stale revision overwrote bytes or ACL.");
            var secondStore = new OwnerSmbConnectionProfiles(profilesRoot, sid, () => current);
            using (var held = new FileStream(Path.Combine(profilesRoot, ".smb-demo.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Check(!secondStore.Save(profile, 1).Ok && store.Read("demo").Revision == 1,
                    "A second writer bypassed the cooperative private lock.");
            }
            var updated = store.Save(profile with { Label = "Updated" }, 1);
            Check(updated is { Ok: true, Revision: 2 },
                "Expected revision update failed: state=" + updated.State + ";error=" + (updated.Error ?? "none") +
                ";actionIssued=" + updated.ActionIssued + ";outcomeUnknown=" + updated.OutcomeUnknown +
                ";revision=" + updated.Revision + ";nativeError=" + (updated.NativeError?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none") + ".");
            // Set an owned fixture preimage above the historical proposed64 bound,
            // proving revisions do not create a forest or have that edit ceiling.
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"revision\":2", "\"revision\":65", StringComparison.Ordinal));
            Check(store.Read("demo").Revision == 65 && store.Save(profile, 65) is { Ok: true, Revision: 66 },
                "Ordinary revision growth was capped.");
            Check(store.Remove("demo", 66) is { Ok: true, State: "removed", Revision: 67, Snapshot: null } &&
                store.Read("demo") is { Ok: true, State: "removed", Revision: 67, Snapshot: null },
                "Removal did not retain a compact latest-revision tombstone.");
            Check(!store.Save(profile, 0).Ok && store.Save(profile, 67) is { Ok: true, Revision: 68 },
                "Tombstone was adopted without expected revision or could not be explicitly restored.");
            Check(Directory.EnumerateFiles(profilesRoot, "smb-demo*").Count() == 1 &&
                !Directory.EnumerateFiles(profilesRoot, "*.tmp").Any(), "Updates retained a revision forest or known temporary.");

            foreach (var id in new[] { "aaa", "bbb", "ccc" })
                Check(store.Save(profile with { Id = id, Label = id }, 0).Ok, "Page fixture creation failed.");
            Check(store.Remove("bbb", 1).Ok, "Page tombstone creation failed.");
            var ids = new List<string>();
            string? cursor = null;
            do
            {
                var page = store.List(cursor, 2);
                Check(page.Ok, "Bounded profile page failed.");
                ids.AddRange(page.Profiles.Select(row => row.Profile.Id));
                Check(page.NextId is null || cursor is null || StringComparer.Ordinal.Compare(page.NextId, cursor) > 0,
                    "Continuation did not advance.");
                cursor = page.NextId;
            } while (cursor is not null);
            Check(ids.SequenceEqual(new[] { "aaa", "ccc", "demo" }), "Paging omitted/duplicated active profiles or exposed tombstone as a profile.");
            Check(!store.List(limit: 0).Ok && !store.List("../bad").Ok, "Invalid paging was admitted.");

            var beforeRead = File.ReadAllBytes(path);
            var originalAccess = new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            try
            {
                var readOnly = new FileSecurity();
                readOnly.SetAccessRuleProtection(true, false);
                foreach (var allowed in new[] { (sid, FileSystemRights.Read | FileSystemRights.Synchronize),
                    ("S-1-5-18", FileSystemRights.FullControl), ("S-1-5-32-544", FileSystemRights.FullControl) })
                    readOnly.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(allowed.Item1), allowed.Item2, AccessControlType.Allow));
                new FileInfo(path).SetAccessControl(readOnly);
                var readAcl = Descriptor(path);
                Check(store.Read("demo") is { Ok: true, Revision: 68 }, "Read-only profile could not be read passively.");
                Check(!store.Save(profile, 68).Ok && beforeRead.AsSpan().SequenceEqual(File.ReadAllBytes(path)) && Descriptor(path) == readAcl,
                    "Read-only leaf was resealed or modified.");
            }
            finally
            {
                var restore = new FileSecurity();
                restore.SetSecurityDescriptorSddlForm(originalAccess, AccessControlSections.Access);
                new FileInfo(path).SetAccessControl(restore);
            }
            var wrongDocument = Path.Combine(profilesRoot, "smb-wrong.v1.json");
            File.WriteAllText(wrongDocument, File.ReadAllText(path));
            Check(!store.Read("wrong").Ok, "A profile with the wrong retained ID was adopted.");
            File.Delete(wrongDocument);

            var unknownPath = Path.Combine(profilesRoot, "smb-uncertain.v1.json");
            var denyAfterPublish = false;
            var unknownStore = new OwnerSmbConnectionProfiles(profilesRoot, sid,
                () => denyAfterPublish && File.Exists(unknownPath) ? peer with { Elevated = true } : peer);
            denyAfterPublish = true;
            var unknown = unknownStore.Save(profile with { Id = "uncertain" }, 0);
            Check(unknown is { Ok: false, ActionIssued: true, OutcomeUnknown: true, State: "unknown" } && File.Exists(unknownPath),
                "Post-publication identity loss was reported as no effect.");
            denyAfterPublish = false;
            Check(unknownStore.Read("uncertain") is { Ok: true, Revision: 1 } &&
                !unknownStore.Save(profile with { Id = "uncertain" }, 0).Ok, "Unknown publication was replayed/adopted.");
            ok = true;
        }
        catch (Exception error)
        { Console.Error.WriteLine("FAIL SMB profile fixture: " + error.GetType().Name + ": " + error.Message); }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); }
            catch (Exception error) { ok = false; Console.Error.WriteLine("FAIL SMB profile fixture cleanup: " + error.GetType().Name); }
        }
        if (!ok) return 1;
        Console.WriteLine("PASS SMB profiles: " + assertions + " closed-model, private-file, revision/tombstone, paging, passive-read, owner/lock/cancellation/unknown assertions (owned filesystem fixture; no network/credentials)");
        return 0;

        void Check(bool condition, string message) { assertions++; if (!condition) throw new InvalidOperationException(message); }
        void ExpectInvalid(Action action)
        {
            assertions++;
            try { action(); } catch (SmbConnectionException) { return; }
            throw new InvalidOperationException("Invalid profile was admitted.");
        }
        static string Descriptor(string path) => new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
    }
}
