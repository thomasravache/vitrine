using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Vitrine.Common.Security;

namespace Vitrine.UnitTests.Security;

/// <summary>
/// <see cref="IFieldProtector"/> e o conversor EF Core <c>IsProtected</c> (contrato da SPEC-0008), sobre um provedor
/// do Data Protection efêmero (chaves só em memória, geradas por execução).
/// </summary>
public sealed class FieldProtectorTests
{
    private const string Plaintext = "token-123";
    private const string Purpose = "ml-token";

    [Fact]
    [Trait("spec", "SPEC-0008:UT-02")]
    public void Protect_devolve_valor_diferente_do_texto_claro_e_Unprotect_devolve_o_original()
    {
        // Dado um IFieldProtector
        var protector = NewProtector();

        // Quando Protect("token-123", "ml-token")
        var protectedValue = protector.Protect(Plaintext, Purpose);

        // Então o resultado difere do texto claro (e não o contém) e Unprotect devolve "token-123"
        Assert.False(string.IsNullOrEmpty(protectedValue), "Protect devolveu valor vazio.");
        Assert.DoesNotContain(Plaintext, protectedValue, StringComparison.Ordinal);
        Assert.Equal(Plaintext, protector.Unprotect(protectedValue, Purpose));
    }

    [Fact]
    [Trait("spec", "SPEC-0008:UT-02")]
    public void Conversor_IsProtected_grava_cifrado_le_decifrado_e_o_valor_nao_aparece_no_ToString_da_entidade()
    {
        // Dado uma entidade com o campo marcado com IsProtected("ml-token") (modelo EF Core, sem banco)
        var protector = NewProtector();
        using var context = new ContasContext(protector);
        var property = context.Model.FindEntityType(typeof(ContaMercadoLivre))!.FindProperty(nameof(ContaMercadoLivre.AccessToken))!;
        var converter = property.GetTypeMapping().Converter;
        Assert.True(converter is not null, "IsProtected não instalou conversor na propriedade.");

        // Quando o valor vai para o banco e volta
        var stored = (string)converter.ConvertToProvider(Plaintext)!;
        var read = (string)converter.ConvertFromProvider(stored)!;

        // Então grava cifrado com o propósito do campo e lê o original
        Assert.DoesNotContain(Plaintext, stored, StringComparison.Ordinal);
        Assert.Equal(Plaintext, protector.Unprotect(stored, Purpose));
        Assert.Equal(Plaintext, read);

        // E nem o texto claro nem o valor protegido aparecem no ToString() da entidade
        var conta = new ContaMercadoLivre { Id = Guid.NewGuid(), AccessToken = Plaintext };
        var text = conta.ToString() ?? string.Empty;
        Assert.DoesNotContain(Plaintext, text, StringComparison.Ordinal);
        Assert.DoesNotContain(stored, text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("spec", "SPEC-0008:UT-03")]
    public void Decifrar_com_proposito_diferente_lanca_CryptographicException()
    {
        // Dado um texto protegido com o propósito "ml-token"
        var protector = NewProtector();
        var protectedValue = protector.Protect(Plaintext, Purpose);

        // Quando decifrado com outro propósito, então CryptographicException
        Assert.Throws<CryptographicException>(() => protector.Unprotect(protectedValue, "outro-campo"));
    }

    [Fact]
    [Trait("spec", "SPEC-0008:UT-03")]
    public void Decifrar_texto_adulterado_ou_truncado_lanca_CryptographicException_sem_texto_parcial()
    {
        // Dado um texto protegido
        var protector = NewProtector();
        var protectedValue = protector.Protect(Plaintext, Purpose);

        // Quando um byte do meio é alterado, então CryptographicException (nunca texto parcial)
        var bytes = WebEncoders.Base64UrlDecode(protectedValue);
        bytes[bytes.Length / 2] ^= 0x01;
        var tampered = WebEncoders.Base64UrlEncode(bytes);
        Assert.Throws<CryptographicException>(() => protector.Unprotect(tampered, Purpose));

        // E o mesmo vale para texto truncado ou que nem é um valor protegido
        Assert.Throws<CryptographicException>(() => protector.Unprotect(protectedValue[..^4], Purpose));
        Assert.Throws<CryptographicException>(() => protector.Unprotect("isto não é cifrado", Purpose));
    }

    [Fact]
    [Trait("spec", "SPEC-0008:UT-03")]
    public void Mil_operacoes_de_cifra_e_decifra_tem_p95_abaixo_de_1_ms()
    {
        // Dado um IFieldProtector aquecido
        const int Operations = 1_000;
        var protector = NewProtector();
        for (var i = 0; i < 50; i++)
        {
            _ = protector.Unprotect(protector.Protect(Plaintext, Purpose), Purpose);
        }

        // Quando 1.000 operações de cifra+decifra são medidas uma a uma
        var durations = new double[Operations];
        for (var i = 0; i < Operations; i++)
        {
            var start = Stopwatch.GetTimestamp();
            var roundTrip = protector.Unprotect(protector.Protect(Plaintext, Purpose), Purpose);
            durations[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Assert.Equal(Plaintext, roundTrip);
        }

        // Então o p95 fica abaixo de 1 ms
        Array.Sort(durations);
        var p95 = durations[(int)Math.Ceiling(Operations * 0.95) - 1];
        Assert.True(p95 < 1.0, $"p95 de cifra+decifra = {p95:F3} ms (limite: 1 ms).");
    }

    private static DataProtectionFieldProtector NewProtector() => new(new EphemeralDataProtectionProvider());

    /// <summary>Entidade de teste com um campo sensível (como o token do Mercado Livre dos épicos seguintes).</summary>
    private sealed class ContaMercadoLivre
    {
        public Guid Id { get; set; }

        public string AccessToken { get; set; } = string.Empty;
    }

    /// <summary>Contexto só para construir o modelo (o provedor Npgsql não conecta sem uma consulta).</summary>
    private sealed class ContasContext(IFieldProtector protector) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
            optionsBuilder
                .UseNpgsql("Host=localhost;Database=vitrine_unit")
                .UseFieldProtection(protector);

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<ContaMercadoLivre>().Property(conta => conta.AccessToken).IsProtected(Purpose);
    }
}
