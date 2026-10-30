using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Vitrine.Common.Security;
using Vitrine.IntegrationTests.MercadoLivre.Support;
using Vitrine.IntegrationTests.Support;
using Vitrine.MercadoLivre.Domain.Authorizations;
using Vitrine.MercadoLivre.Infrastructure.Persistence;

namespace Vitrine.IntegrationTests.Security;

/// <summary>
/// Vários hosts da Api no mesmo processo, cada um com o próprio <see cref="IFieldProtector"/> (SPEC-0026): o caminho
/// padrão de cifra de campos (<c>UseFieldProtection</c>/<c>IsProtected</c>) não cria um provedor interno do EF Core por
/// host, então nenhum host emite <c>ManyServiceProvidersCreatedWarning</c> (o EF o trata como erro a partir do 21º).
/// </summary>
/// <remarks>
/// O Identity não tem campo protegido em produção: o lado "Identity" é um contexto de teste registrado no DI do host
/// (como um DbContext de módulo) com <c>UseFieldProtection</c> e o <see cref="IFieldProtector"/> do host, o mesmo
/// caminho da SPEC-0008:IT-04. O lado MercadoLivre é o <see cref="MercadoLivreModuleDbContext"/> do próprio host
/// (<c>code_verifier</c> de uma autorização pendente).
/// </remarks>
[Collection(PostgresTestGroup.Name)]
public sealed class FieldProtectionManyHostsTests(PostgresFixture postgres)
{
    /// <summary>Acima do limite de 20 provedores internos do EF Core.</summary>
    private const int Hosts = 25;

    [Fact]
    [Trait("spec", "SPEC-0026:IT-01")]
    public async Task Vinte_e_cinco_hosts_no_mesmo_processo_gravam_e_leem_campos_protegidos_sem_ManyServiceProvidersCreatedWarning()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var elapsed = Stopwatch.StartNew();
        ApiFactory? previous = null;
        try
        {
            for (var host = 0; host < Hosts; host++)
            {
                // Dado o host N do processo, com o próprio banco, chave mestra e IFieldProtector
                var connectionString = await postgres.CreateDatabaseAsync(cancellationToken);
                var warnings = new ManyServiceProvidersWarnings();
                var factory = MercadoLivreHost.Create(connectionString)
                    .WithServices(services =>
                    {
                        services.AddSingleton<ILoggerProvider>(warnings);
                        services.AddDbContext<HostSecretsContext>((provider, options) => options
                            .UseNpgsql(connectionString)
                            .UseFieldProtection(provider.GetRequiredService<IFieldProtector>()));
                    });
                try
                {
                    var protector = factory.Services.GetRequiredService<IFieldProtector>();

                    // Quando o host grava e lê um campo protegido de cada lado (contexto com UseFieldProtection e
                    // MercadoLivre)
                    var secret = await WriteAndReadSecretAsync(factory, host, cancellationToken);
                    var verifier = await WriteAndReadVerifierAsync(factory, host, cancellationToken);

                    // Então os valores ficam cifrados com o protetor do próprio host
                    Assert.Equal(secret.Plaintext, protector.Unprotect(secret.Raw, HostSecretsContext.Purpose));
                    Assert.Equal(verifier.Plaintext, protector.Unprotect(verifier.Raw, MercadoLivreHost.TokensPurpose));

                    // E não com o do host anterior (nenhum modelo/conversor compartilhado entre hosts)
                    if (previous is not null)
                    {
                        var other = previous.Services.GetRequiredService<IFieldProtector>();
                        Assert.Throws<CryptographicException>(() => other.Unprotect(secret.Raw, HostSecretsContext.Purpose));
                        Assert.Throws<CryptographicException>(() => other.Unprotect(verifier.Raw, MercadoLivreHost.TokensPurpose));
                    }

                    // E o host não emitiu ManyServiceProvidersCreatedWarning (nem como log)
                    Assert.True(warnings.Count == 0, $"Host {host + 1} de {Hosts} emitiu ManyServiceProvidersCreatedWarning:\n{warnings}");
                }
                catch (InvalidOperationException exception) when (exception.Message.Contains(nameof(CoreEventId.ManyServiceProvidersCreatedWarning), StringComparison.Ordinal))
                {
                    Assert.Fail($"Host {host + 1} de {Hosts} emitiu ManyServiceProvidersCreatedWarning (EF Core o trata como erro): {exception.Message}");
                }
                finally
                {
                    if (previous is not null)
                    {
                        await previous.DisposeAsync();
                    }

                    previous = factory;
                }
            }
        }
        finally
        {
            if (previous is not null)
            {
                await previous.DisposeAsync();
            }

            TestContext.Current.SendDiagnosticMessage($"SPEC-0026:IT-01: {Hosts} hosts em {elapsed.Elapsed.TotalSeconds:F1} s.");
        }
    }

    private static async Task<(string Plaintext, string Raw)> WriteAndReadSecretAsync(ApiFactory factory, int host, CancellationToken cancellationToken)
    {
        var plaintext = $"segredo-do-host-{host}-{Guid.NewGuid():N}";
        var id = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<HostSecretsContext>();
            await context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(cancellationToken);
            context.Secrets.Add(new HostSecret { Id = id, Secret = plaintext });
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<HostSecretsContext>();
            var stored = await context.Secrets.AsNoTracking().SingleAsync(secret => secret.Id == id, cancellationToken);
            Assert.Equal(plaintext, stored.Secret);
        }

        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"select secret from {HostSecretsContext.Schema}.{HostSecretsContext.Table} where id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        var raw = (string)(await command.ExecuteScalarAsync(cancellationToken))!;
        Assert.DoesNotContain(plaintext, raw, StringComparison.Ordinal);
        return (plaintext, raw);
    }

    private static async Task<(string Plaintext, string Raw)> WriteAndReadVerifierAsync(ApiFactory factory, int host, CancellationToken cancellationToken)
    {
        var plaintext = $"verifier-do-host-{host}-{Guid.NewGuid():N}";
        var authorization = PendingAuthorization.Create(
            Guid.NewGuid(), Guid.NewGuid(), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), plaintext, DateTimeOffset.UtcNow);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MercadoLivreModuleDbContext>();
            context.PendingAuthorizations.Add(authorization);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<MercadoLivreModuleDbContext>();
            var stored = await context.PendingAuthorizations.AsNoTracking().SingleAsync(p => p.Id == authorization.Id, cancellationToken);
            Assert.Equal(plaintext, stored.CodeVerifier);
        }

        var row = Assert.Single(
            await MercadoLivreDatabase.ReadPendingAuthorizationsAsync(factory.ConnectionString, cancellationToken),
            row => row.Id == authorization.Id);
        Assert.DoesNotContain(plaintext, row.CodeVerifier, StringComparison.Ordinal);
        return (plaintext, row.CodeVerifier);
    }

    /// <summary>Entidade só de teste com um campo protegido.</summary>
    private sealed class HostSecret
    {
        public Guid Id { get; set; }

        public string Secret { get; set; } = string.Empty;
    }

    /// <summary>Contexto de teste registrado no DI do host, com <c>UseFieldProtection</c> e o protetor do host.</summary>
    private sealed class HostSecretsContext(DbContextOptions<HostSecretsContext> options) : DbContext(options)
    {
        public const string Purpose = "teste-segredo-do-host";
        public const string Schema = "security_many_hosts";
        public const string Table = "host_secrets";

        public DbSet<HostSecret> Secrets => Set<HostSecret>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema(Schema);
            modelBuilder.Entity<HostSecret>(secret =>
            {
                secret.ToTable(Table);
                secret.Property(x => x.Id).HasColumnName("id");
                secret.Property(x => x.Secret).HasColumnName("secret").IsProtected(Purpose);
            });
        }
    }

    /// <summary>Registros de <c>ManyServiceProvidersCreatedWarning</c> no log do host (caso o EF só o registre).</summary>
    private sealed class ManyServiceProvidersWarnings : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _records = new();

        public int Count => _records.Count;

        public ILogger CreateLogger(string categoryName) => new Logger(_records);

        public void Dispose()
        {
        }

        public override string ToString() => string.Join('\n', _records);

        private sealed class Logger(ConcurrentQueue<string> records) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                ArgumentNullException.ThrowIfNull(formatter);
                if (eventId.Id == CoreEventId.ManyServiceProvidersCreatedWarning.Id)
                {
                    records.Enqueue($"[{logLevel}] {formatter(state, exception)}");
                }
            }
        }
    }
}
