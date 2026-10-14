namespace Vitrine.UnitTests;

/// <summary>
/// Teste de fumaça do harness (SPEC-0003:UT-01): prova que o projeto é descoberto pelo
/// Microsoft Testing Platform e que a convenção de tag <c>[Trait("spec", "SPEC-NNNN:&lt;ID&gt;")]</c> chega ao runner.
/// </summary>
public sealed class HarnessSmokeTests
{
    [Fact]
    [Trait("spec", "SPEC-0003:UT-01")]
    public void Teste_de_exemplo_e_descoberto_e_carrega_a_tag_da_spec()
    {
        // Dado o projeto Vitrine.UnitTests; quando executado
        var test = TestContext.Current.Test;

        // Então este teste foi descoberto e carrega a tag da spec
        Assert.NotNull(test);
        Assert.True(test.Traits.TryGetValue("spec", out var specs), "Trait 'spec' ausente no teste descoberto.");
        Assert.Contains("SPEC-0003:UT-01", specs);
        Assert.Fail("Quebrado de propósito: PR de prova do CI (IT-02), fechado sem merge.");
    }
}
