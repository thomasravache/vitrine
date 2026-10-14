using Npgsql;

namespace Vitrine.IntegrationTests.Identity.Support;

/// <summary>Leituras diretas no PostgreSQL do teste (estado persistido pelo módulo Identity e pelo outbox).</summary>
internal static class IdentityDatabase
{
    /// <summary>Linhas de <c>identity.workspaces</c> (Plano de Rollout da SPEC-0006).</summary>
    public static Task<long> CountWorkspacesAsync(string connectionString, CancellationToken cancellationToken) =>
        ScalarAsync(connectionString, "select count(*) from identity.workspaces", cancellationToken);

    /// <summary>Linhas de <c>identity.workspace_members</c> (Plano de Rollout da SPEC-0006).</summary>
    public static Task<long> CountWorkspaceMembersAsync(string connectionString, CancellationToken cancellationToken) =>
        ScalarAsync(connectionString, "select count(*) from identity.workspace_members", cancellationToken);

    /// <summary>
    /// Envelopes <c>WorkspaceCreated</c> no armazenamento durável do Wolverine (outbox/inbox, em qualquer schema),
    /// opcionalmente só os do workspace informado.
    /// </summary>
    public static async Task<long> CountWorkspaceCreatedEnvelopesAsync(
        string connectionString, Guid? workspaceId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var tables = new List<string>();
        await using (var find = new NpgsqlCommand(
            """
            select quote_ident(table_schema) || '.' || quote_ident(table_name)
            from information_schema.tables
            where table_name in ('wolverine_incoming_envelopes', 'wolverine_outgoing_envelopes')
            """,
            connection))
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add(reader.GetString(0));
            }
        }

        Assert.True(tables.Count > 0, "Armazenamento durável do Wolverine (tabelas de envelopes) não existe no banco.");

        // Nomes de tabela vindos do catálogo e citados com quote_ident; o workspace vai como parâmetro.
#pragma warning disable CA2100
        var sql = "select count(*) from ("
            + string.Join(" union all ", tables.Select(table => $"select message_type, body from {table}"))
            + ") envelopes where message_type ilike '%workspace%created%'"
            + " and (@workspace = '' or position(@workspace in convert_from(body, 'UTF8')) > 0)";
        await using var count = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        count.Parameters.AddWithValue("workspace", workspaceId?.ToString("D") ?? string.Empty);
        return (long)(await count.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
