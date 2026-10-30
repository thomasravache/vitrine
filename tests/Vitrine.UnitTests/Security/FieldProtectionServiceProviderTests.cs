using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vitrine.Common.Security;

namespace Vitrine.UnitTests.Security;

/// <summary>
/// <c>UseFieldProtection</c> com vários <see cref="IFieldProtector"/> no mesmo processo (SPEC-0026): cada contexto cifra
/// com o próprio protetor e o EF Core não cria um provedor de serviços interno por protetor.
/// </summary>
/// <remarks>
/// O provedor interno é observado por um serviço singleton dele (<see cref="IModelSource"/>), resolvido pelo provedor
/// do contexto (<see cref="IInfrastructure{T}"/>): contextos com o mesmo provedor interno recebem a mesma instância.
/// Isso não depende do cache global de provedores do EF (que o aviso <c>ManyServiceProvidersCreatedWarning</c> conta
/// para o processo inteiro) e não o enche, então não afeta outros testes.
/// </remarks>
public sealed class FieldProtectionServiceProviderTests
{
    private const string Plaintext = "token-123";
    private const string Purpose = "ml-token";
    private const int Protectors = 5;

    [Fact]
    [Trait("spec", "SPEC-0026:UT-01")]
    public void Contextos_com_protetores_distintos_cifram_cada_um_com_o_seu_e_compartilham_o_provedor_interno_do_EF()
    {
        // Dado protetores distintos (chaves efêmeras diferentes), cada um ligado a um contexto com UseFieldProtection
        var protectors = Enumerable.Range(0, Protectors).Select(_ => NewProtector()).ToList();
        var contexts = protectors.Select(protector => new SecretsContext(protector)).ToList();
        try
        {
            // Quando cada contexto grava e lê o campo protegido (conversor do modelo dele)
            for (var i = 0; i < Protectors; i++)
            {
                var converter = ConverterOf(contexts[i]);
                var stored = (string)converter.ConvertToProvider(Plaintext)!;
                var read = (string)converter.ConvertFromProvider(stored)!;

                // Então grava cifrado com o próprio protetor e lê o original
                Assert.DoesNotContain(Plaintext, stored, StringComparison.Ordinal);
                Assert.Equal(Plaintext, read);
                Assert.Equal(Plaintext, protectors[i].Unprotect(stored, Purpose));

                // E não com o protetor de outro contexto (nada de modelo/conversor compartilhado entre protetores)
                var other = protectors[(i + 1) % Protectors];
                Assert.Throws<CryptographicException>(() => other.Unprotect(stored, Purpose));
            }

            // E um contexto novo com um protetor já usado continua com o dele
            using (var again = new SecretsContext(protectors[0]))
            {
                var stored = (string)ConverterOf(again).ConvertToProvider(Plaintext)!;
                Assert.Equal(Plaintext, protectors[0].Unprotect(stored, Purpose));
                Assert.Throws<CryptographicException>(() => protectors[1].Unprotect(stored, Purpose));

                // (a medida funciona: o mesmo protetor já reaproveita o provedor interno)
                Assert.Same(InternalProviderOf(contexts[0]), InternalProviderOf(again));
            }

            // E o EF Core não criou um provedor de serviços interno por protetor: todos usam o mesmo
            var internalProviders = contexts.Select(InternalProviderOf).Distinct(ReferenceEqualityComparer.Instance).Count();
            Assert.True(
                internalProviders == 1,
                $"{Protectors} protetores criaram {internalProviders} provedores de serviços internos do EF Core (esperado: 1). "
                + "Com mais de 20 hosts no mesmo processo isso dispara ManyServiceProvidersCreatedWarning.");
        }
        finally
        {
            foreach (var context in contexts)
            {
                context.Dispose();
            }
        }
    }

    private static DataProtectionFieldProtector NewProtector() => new(new EphemeralDataProtectionProvider());

    private static Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter ConverterOf(DbContext context)
    {
        var property = context.Model.FindEntityType(typeof(Secret))!.FindProperty(nameof(Secret.Value))!;
        var converter = property.GetTypeMapping().Converter;
        Assert.True(converter is not null, "IsProtected não instalou conversor na propriedade.");
        return converter;
    }

    /// <summary>Um singleton do provedor interno do EF Core: a mesma instância indica o mesmo provedor.</summary>
    private static object InternalProviderOf(DbContext context) => context.GetService<IModelSource>();

    private sealed class Secret
    {
        public Guid Id { get; set; }

        public string Value { get; set; } = string.Empty;
    }

    /// <summary>Contexto só para construir o modelo (o provedor Npgsql não conecta sem uma consulta).</summary>
    private sealed class SecretsContext(IFieldProtector protector) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder
                .UseNpgsql("Host=localhost;Database=vitrine_unit")
                .UseFieldProtection(protector);

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Secret>().Property(secret => secret.Value).IsProtected(Purpose);
    }
}
