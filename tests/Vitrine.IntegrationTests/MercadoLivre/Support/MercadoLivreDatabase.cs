using Npgsql;

namespace Vitrine.IntegrationTests.MercadoLivre.Support;

/// <summary>Linha crua de <c>mercadolivre.accounts</c> (colunas do contrato da SPEC-0020).</summary>
internal sealed record AccountRow(Guid Id, Guid WorkspaceId, long MlUserId, string Nickname, string SiteId, string Status, string AccessToken, string RefreshToken);

/// <summary>Linha crua de <c>mercadolivre.pending_authorizations</c>.</summary>
internal sealed record PendingAuthorizationRow(Guid Id, Guid WorkspaceId, Guid UserId, string StateHash, string CodeVerifier, DateTimeOffset ExpiresAt, DateTimeOffset? UsedAt);

/// <summary>Leituras e preparações diretas no PostgreSQL do teste (schemas <c>mercadolivre</c> e <c>identity</c>, outbox).</summary>
internal static class MercadoLivreDatabase
{
    public static async Task<long> CountAccountsAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var command = new NpgsqlCommand("select count(*) from mercadolivre.accounts", connection);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public static async Task<List<AccountRow>> ReadAccountsAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select id, workspace_id, ml_user_id, nickname, site_id, status::text, access_token::text, refresh_token::text
            from mercadolivre.accounts
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<AccountRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AccountRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                reader.IsDBNull(7) ? string.Empty : reader.GetString(7)));
        }

        return rows;
    }

    /// <summary>Todo o texto da linha da conta (todas as colunas), para procurar valores em claro.</summary>
    public static Task<string> DumpAsync(string connectionString, string table, CancellationToken cancellationToken) =>
        TextOfAsync(connectionString, $"select coalesce(string_agg(t::text, E'\\n'), '') from mercadolivre.{table} t", cancellationToken);

    public static async Task<List<PendingAuthorizationRow>> ReadPendingAuthorizationsAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var command = new NpgsqlCommand(
            "select id, workspace_id, user_id, state_hash::text, code_verifier::text, expires_at, used_at from mercadolivre.pending_authorizations",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<PendingAuthorizationRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PendingAuthorizationRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return rows;
    }

    /// <summary>
    /// Envelopes do evento (<paramref name="messageType"/>, ex.: <c>MercadoLivreAccountConnected</c>) no armazenamento
    /// durável do Wolverine (outbox/inbox, em qualquer schema), opcionalmente só os da conta informada.
    /// </summary>
    public static async Task<long> CountEnvelopesAsync(
        string connectionString, string messageType, Guid? accountId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);

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

        // Nomes de tabela vindos do catálogo e citados com quote_ident; o tipo e a conta vão como parâmetros.
#pragma warning disable CA2100
        var sql = "select count(*) from ("
            + string.Join(" union all ", tables.Select(table => $"select message_type, body from {table}"))
            + ") envelopes where message_type ilike @type"
            // O corpo do envelope é binário: busca a conta por bytes (texto UTF-8 ou os 16 bytes do GUID), sem decodificar.
            + " and (@any or position(@idText in body) > 0 or position(@idBytes in body) > 0)";
        await using var count = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        count.Parameters.AddWithValue("type", $"%{messageType}%");
        count.Parameters.AddWithValue("any", accountId is null);
        count.Parameters.AddWithValue("idText", System.Text.Encoding.UTF8.GetBytes(accountId?.ToString("D") ?? "-"));
        count.Parameters.AddWithValue("idBytes", (accountId ?? Guid.Empty).ToByteArray());
        return (long)(await count.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Torna <paramref name="userId"/> um <c>Member</c> do workspace <paramref name="workspaceId"/> direto no banco (não
    /// há convite nesta versão), removendo o workspace próprio criado no cadastro dele.
    /// </summary>
    public static async Task MakeMemberAsync(string connectionString, Guid userId, Guid ownWorkspaceId, Guid workspaceId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            delete from identity.workspace_members where user_id = @user;
            delete from identity.workspaces where id = @own;
            insert into identity.workspace_members (workspace_id, user_id, role) values (@workspace, @user, 'Member');
            """,
            connection);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("own", ownWorkspaceId);
        command.Parameters.AddWithValue("workspace", workspaceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Deixa <paramref name="userId"/> sem workspace (remove o vínculo e o workspace próprio).</summary>
    public static async Task RemoveWorkspaceAsync(string connectionString, Guid userId, Guid ownWorkspaceId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            delete from identity.workspace_members where user_id = @user;
            delete from identity.workspaces where id = @own;
            """,
            connection);
        command.Parameters.AddWithValue("user", userId);
        command.Parameters.AddWithValue("own", ownWorkspaceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string> TextOfAsync(string connectionString, string sql, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
#pragma warning disable CA2100 // SQL montado só com nomes de tabela fixos dos testes.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
