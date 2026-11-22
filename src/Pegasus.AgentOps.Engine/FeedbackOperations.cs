using System.Text;
using Microsoft.Data.Sqlite;

namespace Pegasus.AgentOps.Engine;

public sealed record FeedbackItem(
    long Seq,
    string Fid,
    string Kind,
    string Area,
    string Summary,
    string? Notes,
    string Status,
    string CreatedUtc,
    string UpdatedUtc);

public sealed record FeedbackPage(IReadOnlyList<FeedbackItem> Items, long? Next, bool More);

public sealed class FeedbackOperations : IDisposable
{
    public const int MaxPageSize = 50;
    public const int MaxSummaryBytes = 512;
    public const int MaxNotesBytes = 8192;
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "bug", "feature", "improvement", "docs", "usability", "performance", "other" };
    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal) { "open", "planned", "deferred", "done", "declined" };
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public FeedbackOperations(string stateRoot)
    {
        Directory.CreateDirectory(stateRoot);
        var path = Path.Combine(Path.GetFullPath(stateRoot), "feedback.db");
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        _connection.Open();
        using var setup = _connection.CreateCommand();
        setup.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; CREATE TABLE IF NOT EXISTS feedback(seq INTEGER PRIMARY KEY AUTOINCREMENT,fid TEXT NOT NULL UNIQUE,kind TEXT NOT NULL,area TEXT NOT NULL,summary TEXT NOT NULL,notes TEXT NULL,status TEXT NOT NULL,created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL); CREATE INDEX IF NOT EXISTS ix_feedback_status_seq ON feedback(status,seq);";
        setup.ExecuteNonQuery();
    }

    public FeedbackItem Submit(string kind, string area, string summary, string? notes)
    {
        ValidateKind(kind); ValidateArea(area); summary = ValidateSummary(summary); notes = ValidateNotes(notes);
        var fid = "F" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
        var now = DateTimeOffset.UtcNow.ToString("O");
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO feedback(fid,kind,area,summary,notes,status,created_utc,updated_utc) VALUES($fid,$kind,$area,$summary,$notes,'open',$created,$updated); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$fid", fid); command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$area", area); command.Parameters.AddWithValue("$summary", summary);
            command.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value); command.Parameters.AddWithValue("$created", now); command.Parameters.AddWithValue("$updated", now);
            var seq = Convert.ToInt64(command.ExecuteScalar());
            return new FeedbackItem(seq, fid, kind, area, summary, notes, "open", now, now);
        }
    }

    public FeedbackItem Get(string fid)
    {
        ValidateFid(fid);
        lock (_gate)
        {
            using var command = _connection.CreateCommand(); command.CommandText = "SELECT seq,fid,kind,area,summary,notes,status,created_utc,updated_utc FROM feedback WHERE fid=$fid"; command.Parameters.AddWithValue("$fid", fid);
            using var reader = command.ExecuteReader(); if (!reader.Read()) throw new AgentOpsException("not_found"); return Read(reader, includeNotes: true);
        }
    }

    public FeedbackItem SetStatus(string fid, string status)
    {
        ValidateFid(fid); ValidateStatus(status); var now = DateTimeOffset.UtcNow.ToString("O");
        lock (_gate)
        {
            using var update = _connection.CreateCommand(); update.CommandText = "UPDATE feedback SET status=$status,updated_utc=$updated WHERE fid=$fid"; update.Parameters.AddWithValue("$fid", fid); update.Parameters.AddWithValue("$status", status); update.Parameters.AddWithValue("$updated", now);
            if (update.ExecuteNonQuery() != 1) throw new AgentOpsException("not_found");
            return Get(fid);
        }
    }

    public FeedbackPage List(string? status, string? kind, string? area, long after, int max, bool includeNotes = false)
    {
        if (status is not null) ValidateStatus(status); if (kind is not null) ValidateKind(kind); if (area is not null) ValidateArea(area);
        if (after < 0 || max is < 1 or > MaxPageSize) throw new AgentOpsException("invalid");
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT seq,fid,kind,area,summary,notes,status,created_utc,updated_utc FROM feedback WHERE seq>$after AND ($status IS NULL OR status=$status) AND ($kind IS NULL OR kind=$kind) AND ($area IS NULL OR area=$area) ORDER BY seq LIMIT $limit";
            command.Parameters.AddWithValue("$after", after); command.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value); command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value); command.Parameters.AddWithValue("$area", (object?)area ?? DBNull.Value); command.Parameters.AddWithValue("$limit", max + 1);
            using var reader = command.ExecuteReader(); var items = new List<FeedbackItem>(); while (reader.Read()) items.Add(Read(reader, includeNotes));
            var more = items.Count > max; if (more) items.RemoveAt(items.Count - 1); return new FeedbackPage(items, items.Count == 0 ? null : items[^1].Seq, more);
        }
    }

    public void Dispose() => _connection.Dispose();

    private static FeedbackItem Read(SqliteDataReader reader, bool includeNotes) => new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), includeNotes && !reader.IsDBNull(5) ? reader.GetString(5) : null, reader.GetString(6), reader.GetString(7), reader.GetString(8));
    private static void ValidateKind(string value) { if (!Kinds.Contains(value)) throw new AgentOpsException("invalid"); }
    private static void ValidateStatus(string value) { if (!Statuses.Contains(value)) throw new AgentOpsException("invalid"); }
    private static void ValidateFid(string value) { if (value.Length != 17 || value[0] != 'F' || !value.AsSpan(1).ContainsOnlyHex()) throw new AgentOpsException("invalid"); }
    private static void ValidateArea(string value) { if (value.Length is < 1 or > 64 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))) throw new AgentOpsException("invalid"); }
    private static string ValidateSummary(string value) { value = value.Trim(); if (value.Length == 0 || value.Contains('\r') || value.Contains('\n') || Encoding.UTF8.GetByteCount(value) > MaxSummaryBytes) throw new AgentOpsException("invalid"); return value; }
    private static string? ValidateNotes(string? value) { if (value is null) return null; value = value.Trim(); if (value.Length == 0) return null; if (value.IndexOf('\0') >= 0 || Encoding.UTF8.GetByteCount(value) > MaxNotesBytes) throw new AgentOpsException("invalid"); return value; }
}

internal static class FeedbackHexExtensions
{
    internal static bool ContainsOnlyHex(this ReadOnlySpan<char> value)
    {
        foreach (var character in value) if (!char.IsAsciiHexDigit(character)) return false;
        return true;
    }
}
