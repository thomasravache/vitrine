using SimulatorPkce = Vitrine.MercadoLivreSimulator.Pkce;

namespace Vitrine.UnitTests.MercadoLivreSimulator;

public sealed class SimulatorPkceTests
{
    // RFC 7636, apêndice B: vetor de teste do método S256.
    private const string RfcVerifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string RfcChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    [Fact]
    [Trait("spec", "SPEC-0024:UT-01")]
    public void Verificador_aceita_o_vetor_S256_do_RFC_7636()
    {
        // Dado o verificador PKCE do simulador; quando recebe o vetor do RFC 7636 (apêndice B)
        // Então aceita
        Assert.True(SimulatorPkce.Verify(RfcVerifier, RfcChallenge));
    }

    [Theory]
    [Trait("spec", "SPEC-0024:UT-01")]
    [InlineData("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXK")] // último caractere trocado
    [InlineData("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjX")] // truncado
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM")] // o próprio challenge (método plain)
    [InlineData("")]
    public void Verificador_recusa_verifier_alterado(string verifier)
    {
        // Dado o challenge do RFC 7636; quando o verifier não é o original
        // Então recusa
        Assert.False(SimulatorPkce.Verify(verifier, RfcChallenge));
    }
}
