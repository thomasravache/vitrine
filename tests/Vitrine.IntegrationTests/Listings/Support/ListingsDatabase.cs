using Npgsql;
using Vitrine.IntegrationTests.Support;

namespace Vitrine.IntegrationTests.Listings.Support;

/// <summary>Linha crua de <c>listings.listings</c> (colunas do contrato da SPEC-0022).</summary>
internal sealed record ListingRow(
    Guid AccountId,
    string ItemId,
    Guid WorkspaceId,
    string Title,
    decimal Price,
    string CurrencyId,
    int AvailableQuantity,
    int SoldQuantity,
    string Status,
    string? Permalink,
    string? ThumbnailUrl,
    DateTimeOffset MlLastUpdated,
    Guid? SyncRunId);

/// <summary>Linha crua de <c>listings.sync_states</c>.</summary>
internal sealed record SyncStateRow(
    Guid AccountId,
    Guid WorkspaceId,
    string Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? LastManualAt,
    long ItemCount,
    string? LastError);

/// <summary>Envelope de mensagem no armazenamento durável do Wolverine.</summary>
internal sealed record EnvelopeRow(string Table, string Status, DateTimeOffset? ExecutionTime, int Attempts);

/// <summary>Leituras e preparações diretas no PostgreSQL do teste (schema <c>listings</c> e armazenamento do Wolverine).</summary>
internal static class ListingsDatabase
{
    /// <summary>Exige as tabelas do contrato (migração inicial do schema <c>listings</c>).</summary>
    public static async Task AssertSchemaAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var command = new NpgsqlCommand(
            "select to_regclass('listings.listings') is not null and to_regclass('listings.sync_states') is not null", connection);
        Assert.True(
            (bool)(await command.ExecuteScalarAsync(cancellationToken))!,
            "Tabelas listings.listings e listings.sync_states não existem: falta a migração inicial do schema listings (SPEC-0022).");
    }

    public static async Task<SyncStateRow?> ReadSyncStateAsync(string connectionString, Guid accountId, CancellationToken cancellationToken)
    {
        await AssertSchemaAsync(connectionString, cancellationToken);
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select account_id, workspace_id, status::text, started_at, completed_at, last_manual_at, item_count, last_error::text
            from listings.sync_states where account_id = @account
            """,
            connection);
        command.Parameters.AddWithValue("account", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SyncStateRow(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            NullableTime(reader, 3),
            NullableTime(reader, 4),
            NullableTime(reader, 5),
            Convert.ToInt64(reader.GetValue(6), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    public static async Task<List<ListingRow>> ReadListingsAsync(string connectionString, Guid accountId, CancellationToken cancellationToken)
    {
        await AssertSchemaAsync(connectionString, cancellationToken);
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select account_id, item_id, workspace_id, title, price, currency_id, available_quantity, sold_quantity, status::text,
                   permalink, thumbnail_url, ml_last_updated, sync_run_id
            from listings.listings where account_id = @account order by item_id
            """,
            connection);
        command.Parameters.AddWithValue("account", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ListingRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ListingRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetDecimal(4),
                reader.GetString(5),
                Convert.ToInt32(reader.GetValue(6), System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(7), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetFieldValue<DateTimeOffset>(11),
                reader.IsDBNull(12) ? null : reader.GetGuid(12)));
        }

        return rows;
    }

    /// <summary>Linhas da conta em <c>listings.listings</c> e em <c>listings.sync_states</c>.</summary>
    public static async Task<(long Listings, long SyncStates)> CountRowsAsync(string connectionString, Guid accountId, CancellationToken cancellationToken)
    {
        await AssertSchemaAsync(connectionString, cancellationToken);
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select (select count(*) from listings.listings where account_id = @account),
                   (select count(*) from listings.sync_states where account_id = @account)
            """,
            connection);
        command.Parameters.AddWithValue("account", accountId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>Espera o estado de sincronização da conta satisfazer <paramref name="condition"/>; devolve a linha.</summary>
    public static async Task<SyncStateRow> WaitForSyncStateAsync(
        string connectionString, Guid accountId, Func<SyncStateRow, bool> condition, string because, CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        await AssertSchemaAsync(connectionString, cancellationToken);
        SyncStateRow? last = null;
        try
        {
            await Eventually.TrueAsync(
                async () =>
                {
                    last = await ReadSyncStateAsync(connectionString, accountId, cancellationToken);
                    return last is not null && condition(last);
                },
                timeout ?? TimeSpan.FromSeconds(30),
                because,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Assert.Fail($"{exception.Message} Último sync_states da conta: {(last is null ? "(nenhum)" : last.ToString())}");
        }

        return last!;
    }

    /// <summary>Espera uma sincronização concluir com sucesso (<c>idle</c>) depois de <paramref name="after"/>.</summary>
    public static Task<SyncStateRow> WaitForIdleAsync(
        string connectionString, Guid accountId, DateTimeOffset? after, CancellationToken cancellationToken, TimeSpan? timeout = null) =>
        WaitForSyncStateAsync(
            connectionString,
            accountId,
            state => state.Status == "idle" && state.CompletedAt is { } completed && (after is null || completed > after),
            "a sincronização da conta não concluiu (sync_states idle com completed_at novo)",
            cancellationToken,
            timeout);

    /// <summary>
    /// Envelopes de <paramref name="messageType"/> (ex.: <c>SyncListings</c>) no armazenamento durável do Wolverine
    /// (entrada, saída e mensagens mortas), só os da conta informada.
    /// </summary>
    public static async Task<List<EnvelopeRow>> ReadEnvelopesAsync(
        string connectionString, string messageType, Guid accountId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        var tables = new List<(string Name, string Kind)>();
        await using (var find = new NpgsqlCommand(
            """
            select quote_ident(table_schema) || '.' || quote_ident(table_name), table_name
            from information_schema.tables
            where table_name in ('wolverine_incoming_envelopes', 'wolverine_outgoing_envelopes', 'wolverine_dead_letters')
            """,
            connection))
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        Assert.True(tables.Count > 0, "Armazenamento durável do Wolverine (tabelas de envelopes) não existe no banco.");

        var rows = new List<EnvelopeRow>();
        foreach (var (name, kind) in tables)
        {
            var columns = kind switch
            {
                "wolverine_incoming_envelopes" => "status, execution_time, attempts",
                "wolverine_outgoing_envelopes" => "'Outgoing', null::timestamptz, attempts",
                _ => "'DeadLetter', null::timestamptz, 0",
            };

            // Nome de tabela vindo do catálogo e citado com quote_ident; tipo e conta vão como parâmetros.
#pragma warning disable CA2100
            await using var command = new NpgsqlCommand(
                $"select {columns} from {name} where message_type ilike @type"
                + " and (position(@idText in body) > 0 or position(@idBytes in body) > 0)",
                connection);
#pragma warning restore CA2100
            command.Parameters.AddWithValue("type", $"%{messageType}%");
            command.Parameters.AddWithValue("idText", System.Text.Encoding.UTF8.GetBytes(accountId.ToString("D")));
            command.Parameters.AddWithValue("idBytes", accountId.ToByteArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new EnvelopeRow(
                    kind,
                    reader.GetString(0),
                    NullableTime(reader, 1),
                    Convert.ToInt32(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        return rows;
    }

    /// <summary>
    /// Insere <paramref name="count"/> anúncios sintéticos da conta direto em <c>listings.listings</c> (70% active, 20%
    /// paused, 10% closed; 1 em cada 10 títulos contém <paramref name="marker"/>).
    /// </summary>
    public static async Task InsertSyntheticListingsAsync(
        string connectionString, Guid accountId, Guid workspaceId, int count, string marker, CancellationToken cancellationToken)
    {
        await AssertSchemaAsync(connectionString, cancellationToken);
        await using var connection = await OpenAsync(connectionString, cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            insert into listings.listings (account_id, item_id, workspace_id, title, price, currency_id, available_quantity,
                                           sold_quantity, status, permalink, thumbnail_url, ml_last_updated, sync_run_id, updated_at)
            select @account, 'MLB' || (7000000000 + n)::text, @workspace,
                   case when n % 10 = 0 then 'Produto ' || n || ' ' || @marker || ' tamanho M' else 'Produto ' || n || ' variado' end,
                   (n % 500) + 9.90, 'BRL', n % 50, n % 20,
                   case when n % 10 < 7 then 'active' when n % 10 < 9 then 'paused' else 'closed' end,
                   'https://produto.mercadolivre.com.br/MLB-' || n, 'https://http2.mlstatic.com/D_' || n || '-I.jpg',
                   timestamptz '2026-01-01 00:00:00+00' + (n || ' minutes')::interval, @run, now()
            from generate_series(1, @count) as n
            """,
            connection);
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("workspace", workspaceId);
        command.Parameters.AddWithValue("marker", marker);
        command.Parameters.AddWithValue("run", Guid.NewGuid());
        command.Parameters.AddWithValue("count", count);
        Assert.Equal(count, await command.ExecuteNonQueryAsync(cancellationToken));
        await using var analyze = new NpgsqlCommand("analyze listings.listings", connection);
        await analyze.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTimeOffset? NullableTime(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
