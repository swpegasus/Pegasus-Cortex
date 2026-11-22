using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Pegasus.Cortex.Persistence.PostgreSql.Tests;

/// <summary>
/// Minimal provider-neutral ADO.NET trace double used to verify command ordering and
/// parameter binding without selecting or emulating a PostgreSQL driver.
/// </summary>
internal sealed class TraceDbDataSource : DbDataSource
{
    public override string ConnectionString => "trace://pegasus-cortex";

    public bool ContextIsClean { get; init; } = true;
    public bool MigrationLedgerExists { get; init; }
    public bool FailCommit { get; init; }
    public List<string> Events { get; } = [];
    public List<TraceCommandExecution> Commands { get; } = [];

    protected override DbConnection CreateDbConnection() => new TraceDbConnection(this);

    private object? Execute(string kind, TraceDbCommand command)
    {
        var classification = Classify(command.CommandText);
        Commands.Add(new TraceCommandExecution(
            classification,
            command.CommandText,
            command.Parameters
                .Cast<DbParameter>()
                .Select(parameter => new TraceParameter(
                    parameter.ParameterName,
                    parameter.DbType,
                    parameter.Value is DBNull ? null : parameter.Value))
                .ToArray()));
        Events.Add($"{kind}:{classification}");

        return classification switch
        {
            "clean-context" => ContextIsClean,
            "migration-ledger-exists" => MigrationLedgerExists,
            _ => kind == "nonquery" ? 1 : null
        };
    }

    private static string Classify(string sql)
    {
        // Whole migration batches contain RLS current_setting expressions, so classify
        // their leading schema DDL before matching individual context commands.
        if (sql.Contains("CREATE SCHEMA cortex_meta", StringComparison.Ordinal) ||
            sql.Contains("CREATE SCHEMA cortex_sync", StringComparison.Ordinal) ||
            sql.Contains("CREATE TABLE cortex_data.project_link", StringComparison.Ordinal) ||
            sql.Contains("CREATE POLICY learning_proposal_audit_read_scope", StringComparison.Ordinal) ||
            sql.Contains("CREATE TABLE cortex_data.retained_chain", StringComparison.Ordinal) ||
            sql.Contains("CREATE TABLE cortex_data.knowledge_publication_origin", StringComparison.Ordinal))
            return "migration-sql";
        if (sql.Contains("current_setting('pegasus.cortex.tenant_id'", StringComparison.Ordinal))
            return "clean-context";
        if (sql.Contains("set_config('pegasus.cortex.tenant_id'", StringComparison.Ordinal))
            return "bind-context";
        if (sql.Contains("pg_advisory_xact_lock", StringComparison.Ordinal))
            return "advisory-lock";
        if (sql.Contains("to_regclass('cortex_meta.schema_migrations')", StringComparison.Ordinal))
            return "migration-ledger-exists";
        if (sql.Contains("INSERT INTO cortex_meta.schema_migrations", StringComparison.Ordinal))
            return "migration-ledger-insert";
        if (sql.Contains("operation_marker", StringComparison.Ordinal))
            return "operation";
        return "other";
    }

    private sealed class TraceDbConnection(TraceDbDataSource owner) : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;

        [AllowNull]
        public override string ConnectionString
        {
            get => owner.ConnectionString;
            set { }
        }

        public override string Database => "trace";
        public override string DataSource => "trace";
        public override string ServerVersion => "1";
        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) =>
            ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        public override void Open()
        {
            _state = ConnectionState.Open;
            owner.Events.Add("open");
        }

        public override void Close() => _state = ConnectionState.Closed;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            owner.Events.Add($"begin:{isolationLevel}");
            return new TraceDbTransaction(this, owner, isolationLevel);
        }

        protected override DbCommand CreateDbCommand() => new TraceDbCommand(this, owner);
    }

    private sealed class TraceDbTransaction(
        DbConnection connection,
        TraceDbDataSource owner,
        IsolationLevel isolationLevel) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => isolationLevel;
        protected override DbConnection DbConnection => connection;

        public override void Commit()
        {
            owner.Events.Add("commit");
            if (owner.FailCommit)
                throw new OperationCanceledException("Synthetic commit cancellation.");
        }
        public override void Rollback() => owner.Events.Add("rollback");
    }

    private sealed class TraceDbCommand(
        DbConnection connection,
        TraceDbDataSource owner) : DbCommand
    {
        private readonly TraceDbParameterCollection _parameters = new();
        private DbTransaction? _transaction;

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; } = CommandType.Text;
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection
        {
            get => connection;
            set { }
        }
        protected override DbParameterCollection DbParameterCollection => _parameters;
        protected override DbTransaction? DbTransaction
        {
            get => _transaction;
            set => _transaction = value;
        }

        public override void Cancel() { }
        public override int ExecuteNonQuery() => (int)(owner.Execute("nonquery", this) ?? 0);
        public override object? ExecuteScalar() => owner.Execute("scalar", this);
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new TraceDbParameter();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            owner.Execute("reader", this);
            return new DataTable().CreateDataReader();
        }
    }

    private sealed class TraceDbParameter : DbParameter
    {
        [AllowNull]
        public override string ParameterName { get; set; } = string.Empty;
        [AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;
        public override object? Value { get; set; }
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
        public override bool IsNullable { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() => DbType = DbType.Object;
    }

    private sealed class TraceDbParameterCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _values = [];

        public override int Count => _values.Count;
        public override object SyncRoot => ((ICollection)_values).SyncRoot;
        public override int Add(object value)
        {
            _values.Add(RequireParameter(value));
            return _values.Count - 1;
        }

        public override void AddRange(Array values)
        {
            foreach (var value in values)
                Add(value!);
        }

        public override void Clear() => _values.Clear();
        public override bool Contains(object value) => value is DbParameter parameter && _values.Contains(parameter);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((ICollection)_values).CopyTo(array, index);
        public override IEnumerator GetEnumerator() => _values.GetEnumerator();
        public override int IndexOf(object value) => value is DbParameter parameter ? _values.IndexOf(parameter) : -1;
        public override int IndexOf(string parameterName) => _values.FindIndex(parameter =>
            string.Equals(parameter.ParameterName, parameterName, StringComparison.Ordinal));
        public override void Insert(int index, object value) => _values.Insert(index, RequireParameter(value));
        public override void Remove(object value)
        {
            if (value is DbParameter parameter)
                _values.Remove(parameter);
        }

        public override void RemoveAt(int index) => _values.RemoveAt(index);
        public override void RemoveAt(string parameterName)
        {
            var index = IndexOf(parameterName);
            if (index >= 0)
                RemoveAt(index);
        }

        protected override DbParameter GetParameter(int index) => _values[index];
        protected override DbParameter GetParameter(string parameterName)
        {
            var index = IndexOf(parameterName);
            return index >= 0
                ? _values[index]
                : throw new IndexOutOfRangeException($"Parameter '{parameterName}' does not exist.");
        }

        protected override void SetParameter(int index, DbParameter value) => _values[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value)
        {
            var index = IndexOf(parameterName);
            if (index < 0)
                _values.Add(value);
            else
                _values[index] = value;
        }

        private static DbParameter RequireParameter(object value) =>
            value as DbParameter ?? throw new ArgumentException("A DbParameter is required.", nameof(value));
    }
}

internal sealed record TraceParameter(string Name, DbType Type, object? Value);

internal sealed record TraceCommandExecution(
    string Classification,
    string CommandText,
    IReadOnlyList<TraceParameter> Parameters)
{
    public object? ParameterValue(string name) =>
        Parameters.Single(parameter => string.Equals(parameter.Name, name, StringComparison.Ordinal)).Value;
}
