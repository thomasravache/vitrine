using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Vitrine.MercadoLivre.Application.Connections;

namespace Vitrine.UnitTests.MercadoLivre;

public sealed partial class PkceTests
{
    // RFC 7636, apêndice B: vetor de teste do método S256.
    private const string RfcVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string RfcChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    [Fact]
    [Trait("spec", "SPEC-0020:UT-01")]
    public void Verifier_gerado_tem_64_caracteres_do_alfabeto_do_RFC_7636()
    {
        // Dado o gerador de PKCE/state; quando gera um par
        var secrets = Pkce.Generate();

        // Então o verifier tem 64 caracteres de [A-Za-z0-9-._~]
        Assert.Equal(64, secrets.CodeVerifier.Length);
        Assert.Matches(VerifierAlphabet(), secrets.CodeVerifier);
    }

    [Fact]
    [Trait("spec", "SPEC-0020:UT-01")]
    public void Challenge_e_base64url_do_SHA256_do_verifier_sem_padding_conferido_com_o_vetor_do_RFC_7636()
    {
        // Dado o vetor do RFC 7636, apêndice B; quando o challenge é calculado
        // Então confere com o do RFC
        Assert.Equal(RfcChallenge, Pkce.ChallengeFor(RfcVerifier));

        // E o par gerado traz o challenge do próprio verifier, sem padding nem caracteres de base64 padrão
        var secrets = Pkce.Generate();
        Assert.Equal(Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(secrets.CodeVerifier))), secrets.CodeChallenge);
        Assert.Equal(Pkce.ChallengeFor(secrets.CodeVerifier), secrets.CodeChallenge);
        Assert.DoesNotContain('=', secrets.CodeChallenge);
        Assert.DoesNotContain('+', secrets.CodeChallenge);
        Assert.DoesNotContain('/', secrets.CodeChallenge);
    }

    [Fact]
    [Trait("spec", "SPEC-0020:UT-01")]
    public void State_tem_32_bytes_aleatorios_em_base64url()
    {
        // Dado o gerador; quando gera um par
        var state = Pkce.Generate().State;

        // Então o state é base64url sem padding (43 caracteres) que decodifica em 32 bytes
        Assert.Matches(Base64UrlAlphabet(), state);
        Assert.Equal(43, state.Length);
        Assert.Equal(32, FromBase64Url(state).Length);
    }

    [Fact]
    [Trait("spec", "SPEC-0020:UT-01")]
    public void Duas_geracoes_nunca_se_repetem()
    {
        // Dado muitas gerações seguidas
        var generated = Enumerable.Range(0, 200).Select(_ => Pkce.Generate()).ToList();

        // Então nenhum verifier, challenge ou state se repete, e o state nunca é o verifier
        Assert.Equal(generated.Count, generated.Select(secrets => secrets.CodeVerifier).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(generated.Count, generated.Select(secrets => secrets.CodeChallenge).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(generated.Count, generated.Select(secrets => secrets.State).Distinct(StringComparer.Ordinal).Count());
        Assert.All(generated, secrets => Assert.NotEqual(secrets.State, secrets.CodeVerifier));
    }

    [Fact]
    [Trait("spec", "SPEC-0020:UT-01")]
    public void Texto_dos_segredos_nao_expoe_o_verifier_nem_o_state()
    {
        // Dado um par gerado; quando convertido em texto (ex.: registrado em log por descuido)
        var secrets = Pkce.Generate();
        var text = secrets.ToString();

        // Então não contém o verifier nem o state
        Assert.DoesNotContain(secrets.CodeVerifier, text, StringComparison.Ordinal);
        Assert.DoesNotContain(secrets.State, text, StringComparison.Ordinal);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '='));
    }

    [GeneratedRegex("^[A-Za-z0-9._~-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex VerifierAlphabet();

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Base64UrlAlphabet();
}
