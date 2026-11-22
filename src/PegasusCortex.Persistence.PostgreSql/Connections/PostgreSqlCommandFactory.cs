using System.Data;
using System.Data.Common;

namespace Pegasus.Cortex.Persistence.PostgreSql.Connections;

internal readonly record struct PostgreSqlParameter(string Name, DbType Type, object? Value);

internal static class PostgreSqlCommandFactory
{
    public static DbCommand Create(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        params PostgreSqlParameter[] parameters)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandType = CommandType.Text;
        command.CommandText = sql;
        foreach (var value in parameters)
        {
            if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character != '_'))
            {
                command.Dispose();
                throw new ArgumentException("A PostgreSQL parameter name is invalid.", nameof(parameters));
            }

            var parameter = command.CreateParameter();
            parameter.ParameterName = value.Name;
            parameter.DbType = value.Type;
            parameter.Value = value.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }
}
