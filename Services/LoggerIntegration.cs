using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using LoggerAction.Log.Domain;
using LoggerAction.Log.Helper;
using LoggerAction.Log.Interfaces;
using System.Text;
using System.Text.Json;

namespace LoggerAction.Log.Services;

public class LoggerIntegration(
    IAmazonCloudWatchLogs amazonCloudWatchLogs,
    CloudWatchConfiguration cloudWatchConfiguration) : ILoggerIntegration
{
    // Limite de tamanho de um único evento no CloudWatch Logs.
    private const int MaxBytesPorEvento = 256 * 1024;

    // Folga para o overhead de escape do JSON ao reserializar após o truncamento.
    private const int MargemSeguranca = 2 * 1024;

    private const int MaxTentativas = 3;
    private const string MarcadorTruncamento = "...[truncado pelo LoggerAction]";

    private const int MaxBytesPorCaractereUtf8 = 4;

    /// <summary>
    /// Dispara o envio e retorna imediatamente: a request não espera o CloudWatch.
    /// </summary>
    public Task SendLog(LogRegister logRegister)
    {
        // Task.Run tira a serialização e a assinatura SigV4 da thread da request.
        _ = Task.Run(() => Enviar(logRegister));
        return Task.CompletedTask;
    }

    private async Task Enviar(LogRegister logRegister)
    {
        try
        {
            var request = new PutLogEventsRequest(
                cloudWatchConfiguration.LogGroupName,
                cloudWatchConfiguration.LogStreamName,
                [
                    new InputLogEvent
                    {
                        Message = Serializar(logRegister),
                        Timestamp = DateTime.UtcNow
                    }
                ]);

            for (var tentativa = 1; ; tentativa++)
            {
                try
                {
                    await amazonCloudWatchLogs.PutLogEventsAsync(request);
                    return;
                }
                catch (Exception ex) when (tentativa < MaxTentativas && !EhPermanente(ex))
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, tentativa)));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"-- LoggerAction ERROR -- Registro {logRegister.Id} perdido após {MaxTentativas} tentativa(s). Message: {ex.Message} ### Stack Trace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// Erros que retentar não resolve: insistir só atrasaria o descarte.
    /// Qualquer exceção desconhecida é tratada como transitória e é retentada.
    /// </summary>
    private static bool EhPermanente(Exception ex)
        => ex is ResourceNotFoundException or InvalidParameterException or DataAlreadyAcceptedException;

    /// <summary>
    /// Serializa o registro garantindo o teto de 256 KB por evento. Acima disso a AWS
    /// rejeitaria o envio inteiro, então os bodies são truncados para preservar o
    /// restante do registro (Path, StatusCode, duração, Logs).
    /// </summary>
    internal static string Serializar(LogRegister logRegister)
    {
        var mensagem = JsonSerializer.Serialize(logRegister);

        if (Encoding.UTF8.GetByteCount(mensagem) <= MaxBytesPorEvento)
        {
            return mensagem;
        }

        var requestBody = logRegister.RequestBody;
        var responseBody = logRegister.ResponseBody;

        // Mede o registro sem os bodies para saber quanto de espaço sobra para eles.
        logRegister.AplicarTruncamento(null, null);
        var bytesFixos = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(logRegister));

        // O orçamento é medido no texto cru, mas o JSON escapa o conteúdo depois
        // (aspas viram \", não-ASCII vira \uXXXX). Como a expansão depende do conteúdo,
        // corta, reserializa e confere; se ainda não couber, corta pela metade de novo.
        for (var disponivel = MaxBytesPorEvento - MargemSeguranca - bytesFixos; disponivel > 0; disponivel /= 2)
        {
            var (orcamentoRequest, orcamentoResponse) = DistribuirOrcamento(requestBody, responseBody, disponivel);

            logRegister.AplicarTruncamento(
                Truncar(requestBody, orcamentoRequest),
                Truncar(responseBody, orcamentoResponse));

            mensagem = JsonSerializer.Serialize(logRegister);

            if (Encoding.UTF8.GetByteCount(mensagem) <= MaxBytesPorEvento)
            {
                Console.WriteLine($"-- LoggerAction WARN -- Registro {logRegister.Id} passa de 256 KB. RequestBody/ResponseBody truncados.");
                return mensagem;
            }
        }

        // Nem sem os bodies o registro cabe: a própria lista Logs estoura o limite.
        // Sem cortá-la o evento seria rejeitado pela AWS e o registro se perderia inteiro.
        logRegister.AplicarTruncamento(null, null);

        var logsOriginais = logRegister.Logs;

        for (var mantidas = logsOriginais.Count / 2; mantidas > 0; mantidas /= 2)
        {
            logRegister.AplicarTruncamentoLogs(ReduzirLogs(logsOriginais, mantidas));

            mensagem = JsonSerializer.Serialize(logRegister);

            if (Encoding.UTF8.GetByteCount(mensagem) <= MaxBytesPorEvento)
            {
                Console.WriteLine($"-- LoggerAction WARN -- Registro {logRegister.Id} passa de 256 KB mesmo sem os bodies. Bodies removidos e {logsOriginais.Count - mantidas} entrada(s) de Logs descartada(s).");
                return mensagem;
            }
        }

        // Uma única entrada de Logs já estoura o limite: não há o que preservar.
        logRegister.AplicarTruncamentoLogs([]);

        Console.WriteLine($"-- LoggerAction WARN -- Registro {logRegister.Id} passa de 256 KB. Bodies e Logs removidos.");

        return JsonSerializer.Serialize(logRegister);
    }

    /// <summary>
    /// Mantém as primeiras entradas da lista e sinaliza quantas foram descartadas,
    /// para que quem lê o log no CloudWatch saiba que a sequência está incompleta.
    /// </summary>
    private static List<string> ReduzirLogs(IReadOnlyList<string> logs, int mantidas)
    {
        var reduzidos = logs.Take(mantidas).ToList();

        reduzidos.Add($"{MarcadorTruncamento} {logs.Count - mantidas} entrada(s) de log descartada(s)");

        return reduzidos;
    }

    /// <summary>
    /// Divide o espaço disponível entre os dois bodies. O que couber na metade é
    /// preservado inteiro e a sobra vai para o maior, para truncar o mínimo possível.
    /// </summary>
    private static (int Request, int Response) DistribuirOrcamento(string? requestBody, string? responseBody, int disponivel)
    {
        var tamanhoRequest = requestBody is null ? 0 : Encoding.UTF8.GetByteCount(requestBody);
        var tamanhoResponse = responseBody is null ? 0 : Encoding.UTF8.GetByteCount(responseBody);

        var metade = disponivel / 2;

        if (tamanhoRequest <= metade)
        {
            return (tamanhoRequest, disponivel - tamanhoRequest);
        }

        if (tamanhoResponse <= metade)
        {
            return (disponivel - tamanhoResponse, tamanhoResponse);
        }

        return (metade, disponivel - metade);
    }

    internal static string? Truncar(string? texto, int maxBytes)
    {
        if (texto is null || Encoding.UTF8.GetByteCount(texto) <= maxBytes)
        {
            return texto;
        }

        var limite = maxBytes - Encoding.UTF8.GetByteCount(MarcadorTruncamento);

        // O Convert lança ArgumentException se o buffer de saída não couber nem um
        // caractere. Um scalar UTF-8 ocupa no máximo 4 bytes, então abaixo disso
        // nem o primeiro caractere é garantido: sobra só o marcador.
        if (limite < MaxBytesPorCaractereUtf8)
        {
            return MarcadorTruncamento;
        }

        var buffer = new byte[limite];

        // Convert para na fronteira de caractere, sem quebrar par surrogate nem gerar UTF-8 inválido.
        Encoding.UTF8.GetEncoder().Convert(texto.AsSpan(), buffer.AsSpan(), true, out var charsUsados, out _, out _);

        return string.Concat(texto.AsSpan(0, charsUsados), MarcadorTruncamento);
    }
}
