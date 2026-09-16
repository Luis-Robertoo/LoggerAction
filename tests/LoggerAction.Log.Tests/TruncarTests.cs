using LoggerAction.Log.Services;
using System.Text;

namespace LoggerAction.Log.Tests;

/// <summary>
/// Exercita <see cref="LoggerIntegration.Truncar"/> isoladamente. O ponto crítico é a
/// fronteira de caractere: cortar no meio de um par surrogate ou de uma sequência UTF-8
/// produziria texto que o JsonSerializer não consegue representar.
/// </summary>
public class TruncarTests
{
    private const string Marcador = "...[truncado pelo LoggerAction]";

    [Fact]
    public void QuandoCabeNoLimite_DevolveTextoIntacto()
    {
        const string texto = "abc";

        Assert.Same(texto, LoggerIntegration.Truncar(texto, 3));
        Assert.Same(texto, LoggerIntegration.Truncar(texto, 1024));
    }

    [Fact]
    public void QuandoTextoEhNulo_DevolveNulo()
    {
        Assert.Null(LoggerIntegration.Truncar(null, 1024));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(31)]
    [InlineData(34)]
    public void OrcamentoMenorQueOMarcadorMaisUmCaractere_DevolveSoOMarcador(int maxBytes)
    {
        // O marcador tem 31 bytes. Abaixo de 35 não sobra buffer para nenhum caractere
        // e o Encoder.Convert lançaria ArgumentException em vez de truncar.
        var resultado = LoggerIntegration.Truncar(new string('a', 1000), maxBytes);

        Assert.Equal(Marcador, resultado);
    }

    [Fact]
    public void OrcamentoAbaixoDoMarcador_EstouraNoTamanhoDoMarcador()
    {
        // Limitação aceita: com orçamento menor que 31 bytes o retorno é o marcador
        // inteiro. Quem chama absorve isso na MargemSeguranca de 2 KB.
        var resultado = LoggerIntegration.Truncar(new string('a', 1000), 10);

        Assert.True(Encoding.UTF8.GetByteCount(resultado!) > 10);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(65536)]
    public void TextoAscii_RespeitaOrcamentoEmBytes(int maxBytes)
    {
        var resultado = LoggerIntegration.Truncar(new string('a', 200_000), maxBytes);

        Assert.NotNull(resultado);
        Assert.EndsWith(Marcador, resultado);
        Assert.True(Encoding.UTF8.GetByteCount(resultado) <= maxBytes);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public void TextoAcentuado_NaoQuebraSequenciaUtf8(int maxBytes)
    {
        var resultado = LoggerIntegration.Truncar(string.Concat(Enumerable.Repeat("ação-é-ü", 5000)), maxBytes);

        Assert.NotNull(resultado);
        Assert.True(Encoding.UTF8.GetByteCount(resultado) <= maxBytes);
        Assert.False(TemSurrogateSolto(resultado));
    }

    [Fact]
    public void TextoComEmoji_NuncaQuebraParSurrogate()
    {
        // Emoji ocupa 4 bytes em UTF-8. Varrendo todos os deslocamentos possíveis do
        // orçamento garantimos que a fronteira cai no meio de um par em algum momento.
        var texto = string.Concat(Enumerable.Repeat("🚀", 2000));

        for (var maxBytes = 31; maxBytes <= 120; maxBytes++)
        {
            var resultado = LoggerIntegration.Truncar(texto, maxBytes);

            Assert.NotNull(resultado);
            Assert.True(
                Encoding.UTF8.GetByteCount(resultado) <= maxBytes,
                $"maxBytes={maxBytes} produziu {Encoding.UTF8.GetByteCount(resultado)} bytes");
            Assert.False(TemSurrogateSolto(resultado), $"maxBytes={maxBytes} deixou surrogate solto");
        }
    }

    private static bool TemSurrogateSolto(string texto)
    {
        for (var i = 0; i < texto.Length; i++)
        {
            if (char.IsHighSurrogate(texto[i]))
            {
                if (i + 1 >= texto.Length || !char.IsLowSurrogate(texto[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(texto[i]))
            {
                return true;
            }
        }

        return false;
    }
}
