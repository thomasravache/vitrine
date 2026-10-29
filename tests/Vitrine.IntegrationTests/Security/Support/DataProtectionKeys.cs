using Npgsql;

namespace Vitrine.IntegrationTests.Security.Support;

/// <summary>
/// Anel de chaves do Data Protection persistido no PostgreSQL (tabela do <c>Microsoft.AspNetCore.DataProtection.EntityFrameworkCore</c>:
/// uma linha por chave, com o XML da chave numa coluna <c>xml</c>), lido por SQL cru em qualquer schema.
/// </summary>
internal static class DataProtectionKeys
{
    public static async Task<List<string>> ReadXmlAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var tables = new List<(string Table, string Column)>();
        await using (var find = new NpgsqlCommand(
            """
            select quote_ident(table_schema) || '.' || quote_ident(table_name), quote_ident(column_name)
            from information_schema.columns
            where lower(column_name) = 'xml'
              and replace(lower(table_name), '_', '') like '%dataprotection%'
            """,
            connection))
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                tables.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        Assert.True(tables.Count > 0, "Nenhuma tabela de chaves do Data Protection (coluna xml) no PostgreSQL: as chaves não estão persistidas no banco.");

        var keys = new List<string>();
        foreach (var (table, column) in tables)
        {
            // Nomes vindos do catálogo e citados com quote_ident.
#pragma warning disable CA2100
            await using var select = new NpgsqlCommand($"select {column} from {table}", connection);
#pragma warning restore CA2100
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                keys.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
            }
        }

        return keys;
    }
}
