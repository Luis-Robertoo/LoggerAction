using LoggerAction.Log.Domain;
using LoggerAction.Log.Services;
using System.Text;
using System.Text.Json;

namespace LoggerAction.Log.Tests;

/// <summary>
/// Valida o teto de 256 KB por evento do CloudWatch Logs. O ponto delicado é que o
/// orçamento é calculado em bytes crus mas o JsonSerializer escapa o conteúdo depois:
/// aspas dobram de tamanho e qualquer caractere fora do ASCII vira \uXXXX.
/// </summary>
public class SerializarTests(ITestOutputHelper output)
{
    private const int MaxBytesPorEvento = 256 * 1024;

    [Fact]
    public void RegistroPequeno_NaoEhTruncado()
    {
        const string body = """{"nome":"teste"}""";

        var json = Serializar(CriarRegistro(body, body));

        Assert.Equal(body, LerTexto(json, "RequestBody"));
        Assert.Equal(body, LerTexto(json, "ResponseBody"));
    }

    [Fact]
    public void BodyAsciiGrande_EhTruncado()
    {
        var json = Serializar(CriarRegistro(new string('a', 400_000), null));

        Assert.Contains("truncado pelo LoggerAction", LerTexto(json, "RequestBody"));
        Assert.Null(LerTexto(json, "ResponseBody"));
    }

    [Fact]
    public void BodySoDeAspas_AbsorveExpansaoDoEscape()
    {
        // Cada " vira \" na serialização: 2x sobre o tamanho cru.
        var json = Serializar(CriarRegistro(new string('"', 300_000), null));

        Assert.Contains("truncado pelo LoggerAction", LerTexto(json, "RequestBody"));
    }

    [Fact]
    public void BodyAcentuado_AbsorveExpansaoDoEscape()
    {
        // 'é' ocupa 2 bytes em UTF-8 e vira \u00e9 (6 bytes) no JSON: 3x sobre o cru.
        var json = Serializar(CriarRegistro(new string('é', 200_000), null));

        Assert.Contains("truncado pelo LoggerAction", LerTexto(json, "RequestBody"));
    }

    [Fact]
    public void BodyComEmoji_PreservaParesSurrogate()
    {
        var json = Serializar(CriarRegistro(string.Concat(Enumerable.Repeat("🚀", 100_000)), null));

        var requestBody = LerTexto(json, "RequestBody");

        Assert.NotNull(requestBody);
        Assert.DoesNotContain('\uFFFD', requestBody);
        Assert.Contains("truncado pelo LoggerAction", requestBody);
    }

    [Fact]
    public void AmbosOsBodiesGrandes_SaoTruncados()
    {
        var json = Serializar(CriarRegistro(new string('a', 400_000), new string('b', 400_000)));

        Assert.Contains("truncado pelo LoggerAction", LerTexto(json, "RequestBody"));
        Assert.Contains("truncado pelo LoggerAction", LerTexto(json, "ResponseBody"));
    }

    [Fact]
    public void UmBodyPequenoEOutroGrande_PreservaOPequenoInteiro()
    {
        const string pequeno = """{"erro":"algo deu errado"}""";

        var json = Serializar(CriarRegistro(new string('a', 400_000), pequeno));

        Assert.Contains("truncado pelo LoggerAction", LerTexto(json, "RequestBody"));
        Assert.Equal(pequeno, LerTexto(json, "ResponseBody"));
    }

    [Fact]
    public void LogsEnorme_ZeraOsBodiesECortaALista()
    {
        var logs = Enumerable.Range(0, 3000).Select(i => $"{i}: {new string('x', 100)}").ToList();

        var json = Serializar(CriarRegistro("request", "response", logs));

        Assert.Null(LerTexto(json, "RequestBody"));
        Assert.Null(LerTexto(json, "ResponseBody"));

        var restantes = json.GetProperty("Logs").EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert.True(restantes.Count < logs.Count);
        Assert.StartsWith("0: ", restantes[0]);
        Assert.Contains("entrada(s) de log descartada(s)", restantes[^1]);
    }

    /// <summary>
    /// Uma única entrada de log maior que o limite não tem o que preservar:
    /// o registro vai sem Logs, mas vai.
    /// </summary>
    [Fact]
    public void UmaUnicaEntradaDeLogEstourandoOLimite_EsvaziaALista()
    {
        var json = Serializar(CriarRegistro(null, null, [new string('x', 400_000)]));

        Assert.Empty(json.GetProperty("Logs").EnumerateArray());
    }

    private static LogRegister CriarRegistro(string? requestBody, string? responseBody, IReadOnlyList<string>? logs = null)
    {
        var registro = new LogRegister(logs ?? []);

        // AplicarTruncamento é o único ponto de escrita dos bodies: aqui serve de setter.
        registro.AplicarTruncamento(requestBody, responseBody);

        return registro;
    }

    /// <summary>
    /// Serializa e cobra as duas invariantes que valem para qualquer entrada:
    /// o evento cabe em 256 KB e o resultado é JSON válido.
    /// </summary>
    private JsonElement Serializar(LogRegister registro)
    {
        var mensagem = LoggerIntegration.Serializar(registro);
        var bytes = Encoding.UTF8.GetByteCount(mensagem);

        output.WriteLine($"{bytes:N0} bytes ({bytes * 100d / MaxBytesPorEvento:N1}% do limite)");

        Assert.True(bytes <= MaxBytesPorEvento, $"Evento com {bytes:N0} bytes excede o limite de {MaxBytesPorEvento:N0}.");

        return JsonDocument.Parse(mensagem).RootElement;
    }

    private static string? LerTexto(JsonElement raiz, string propriedade)
        => raiz.GetProperty(propriedade).GetString();
}
