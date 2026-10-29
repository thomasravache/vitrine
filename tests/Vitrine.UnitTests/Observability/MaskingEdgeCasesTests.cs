using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Vitrine.ServiceDefaults.Logging;

namespace Vitrine.UnitTests.Observability;

/// <summary>
/// UT-01 (SPEC-0007), guardas: o console JSON dos padrões de serviço (fora do desenvolvimento) aplica o mesmo
/// mascaramento em mensagem, exceção, atributos e escopos e carrega o <c>trace_id</c>; e as regras do
/// <see cref="SensitiveDataMasker"/> mantêm intactos valores que não carregam dado sensível (nulos, tipos de valor).
/// </summary>
public sealed class MaskingEdgeCasesTests
{
    private const string FormatterName = "vitrine-json";
    private const string Email = "marina.souza@example.com";
    private const string Password = "senha-forte-2026";
    private const string Category = "Vitrine.Testes.Console";

    [Fact]
    [Trait("spec", "SPEC-0007:UT-01")]
    public void Console_JSON_mascara_mensagem_excecao_atributos_e_escopos_e_inclui_trace_id()
    {
        // Dado o formatador JSON do console registrado pelos padrões de serviço em produção, com escopos ativos
        using var host = BuildProductionHost();
        var formatter = JsonConsoleFormatter(host);
        var scopes = new LoggerExternalScopeProvider();
        using var requestScope = scopes.Push(new List<KeyValuePair<string, object?>> { new("email", Email), new("pedido", "42") });
        using var textScope = scopes.Push($"sessao de {Email}");
        using var activity = new Activity("Vitrine.Testes.Console").Start();

        // Quando um registro com e-mail e senha na mensagem, nos atributos, nos escopos e na exceção é escrito
        var state = new List<KeyValuePair<string, object?>>
        {
            new("email", Email),
            new("password", Password),
            new("tentativas", 3),
            new("{OriginalFormat}", "Login de {email} com {password} ({tentativas})"),
        };
        var entry = new LogEntry<List<KeyValuePair<string, object?>>>(
            LogLevel.Error,
            Category,
            new EventId(7),
            state,
            new InvalidOperationException($"Falha ao autenticar {Email}"),
            (_, _) => $"Login de {Email} com {Password} (3)");
        using var output = new StringWriter();
        formatter.Write(in entry, scopes, output);

        // Então sai uma linha JSON sem o e-mail nem a senha em lugar nenhum
        var line = output.ToString().Trim();
        Assert.DoesNotContain(Email, line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, line, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;

        // E com trace_id/span_id da atividade corrente, exceção (mascarada), atributos e escopos mascarados
        Assert.Equal(activity.TraceId.ToHexString(), root.GetProperty("trace_id").GetString());
        Assert.Equal(activity.SpanId.ToHexString(), root.GetProperty("span_id").GetString());
        Assert.Contains("InvalidOperationException", root.GetProperty("exception").GetString(), StringComparison.Ordinal);
        var attributes = root.GetProperty("attributes");
        Assert.Equal(SensitiveDataMasker.Mask, attributes.GetProperty("email").GetString());
        Assert.Equal(SensitiveDataMasker.Mask, attributes.GetProperty("password").GetString());
        Assert.Equal("3", attributes.GetProperty("tentativas").GetString());
        var scopeEntries = root.GetProperty("scopes").EnumerateArray().ToList();
        var properties = Assert.Single(scopeEntries, scope => scope.ValueKind == JsonValueKind.Object);
        Assert.Equal(SensitiveDataMasker.Mask, properties.GetProperty("email").GetString());
        Assert.Equal("42", properties.GetProperty("pedido").GetString());
        var text = Assert.Single(scopeEntries, scope => scope.ValueKind == JsonValueKind.String);
        Assert.Equal($"sessao de {SensitiveDataMasker.Mask}", text.GetString());
    }

    [Fact]
    [Trait("spec", "SPEC-0007:UT-01")]
    public void Console_JSON_nao_escreve_registro_sem_mensagem_nem_excecao()
    {
        // Dado o formatador JSON do console dos padrões de serviço
        using var host = BuildProductionHost();
        var formatter = JsonConsoleFormatter(host);

        // Quando o registro não tem mensagem nem exceção
        var entry = new LogEntry<string>(LogLevel.Information, Category, new EventId(8), "estado", null, (_, _) => null!);
        using var output = new StringWriter();
        formatter.Write(in entry, new LoggerExternalScopeProvider(), output);

        // Então nada é escrito (sem linhas JSON vazias no stdout do container)
        Assert.Equal(string.Empty, output.ToString());
    }

    [Theory]
    [Trait("spec", "SPEC-0007:UT-01")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tentativas")]
    [InlineData("userId")]
    public void Nome_ausente_ou_neutro_nao_e_sensivel(string? name) =>
        // Dado/Quando um nome de propriedade ausente ou neutro; Então não é tratado como sensível
        Assert.False(SensitiveDataMasker.IsSensitiveName(name));

    [Fact]
    [Trait("spec", "SPEC-0007:UT-01")]
    public void Propriedades_nulas_tipos_de_valor_e_objetos_sem_texto_sao_mantidos_como_vieram()
    {
        // Dado propriedades sem dado sensível a vazar: sensível nula, número, data, Guid e objeto sem texto
        var when = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        var id = Guid.Parse("4f8e2c1a-9b7d-4e3f-8a6b-1c2d3e4f5a6b");
        var properties = new List<KeyValuePair<string, object?>>
        {
            new("email", null),
            new("tentativas", 3),
            new("quando", when),
            new("userId", id),
            new("resultado", new WithoutText()),
        };
        var sensitiveValues = new List<string>();

        // Quando o mascaramento é aplicado
        var masked = SensitiveDataMasker.MaskProperties(properties, sensitiveValues);

        // Então nada muda (null = lista original preservada) e nenhum valor é marcado como sensível
        Assert.Null(masked);
        Assert.Empty(sensitiveValues);
        Assert.Null(SensitiveDataMasker.Text(null));
    }

    private static IHost BuildProductionHost()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Vitrine.UnitTests",
            EnvironmentName = Environments.Production,
            DisableDefaults = true,
        });
        builder.AddServiceDefaults();
        return builder.Build();
    }

    private static ConsoleFormatter JsonConsoleFormatter(IHost host)
    {
        var formatter = host.Services.GetServices<ConsoleFormatter>().SingleOrDefault(candidate => candidate.Name == FormatterName);
        Assert.True(formatter is not null, $"Os padrões de serviço não registraram o formatador de console \"{FormatterName}\" em produção.");
        return formatter;
    }

    /// <summary>Objeto cujo texto é nulo (ex.: tipo de terceiro com <c>ToString</c> sem valor).</summary>
    private sealed class WithoutText
    {
        public override string? ToString() => null;
    }
}
