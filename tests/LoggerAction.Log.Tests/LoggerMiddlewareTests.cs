using LoggerAction.Log.Domain;
using LoggerAction.Log.Helper;
using LoggerAction.Log.Interfaces;
using LoggerAction.Log.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Text;
using System.Text.Json;

namespace LoggerAction.Log.Tests;

/// <summary>
/// Sobe o pipeline real do ASP.NET Core em memória com o middleware instalado.
/// O CloudWatch é substituído por um fake, então o que se verifica aqui é o que o
/// middleware captura e, igualmente importante, o que ele devolve ao cliente: ele
/// troca o Response.Body por um MemoryStream e copia de volta.
/// </summary>
public class LoggerMiddlewareTests
{
    [Fact]
    public async Task VerboExcluido_NaoGeraRegistroMasDeixaARequestPassar()
    {
        var integracao = new LoggerIntegrationFake();

        using var host = await CriarHost(integracao, ["OPTIONS"], async context => await context.Response.WriteAsync("ok"));

        var resposta = await host.GetTestClient().SendAsync(new HttpRequestMessage(HttpMethod.Options, "/recurso"));

        Assert.Equal("ok", await resposta.Content.ReadAsStringAsync());
        Assert.Empty(integracao.Registros);
    }

    [Fact]
    public async Task VerboExcluido_ComparacaoIgnoraMaiusculas()
    {
        var integracao = new LoggerIntegrationFake();

        using var host = await CriarHost(integracao, ["options"], _ => Task.CompletedTask);

        await host.GetTestClient().SendAsync(new HttpRequestMessage(HttpMethod.Options, "/recurso"));

        Assert.Empty(integracao.Registros);
    }

    [Fact]
    public async Task VerboNaoExcluido_GeraRegistro()
    {
        var integracao = new LoggerIntegrationFake();

        using var host = await CriarHost(integracao, ["OPTIONS"], _ => Task.CompletedTask);

        await host.GetTestClient().GetAsync("/recurso?filtro=1");

        var registro = Assert.Single(integracao.Registros);

        Assert.Equal("GET", registro.HttpMethod);
        Assert.Contains("/recurso?filtro=1", registro.Path);
        Assert.Equal("API", registro.TipoProcessamento);
    }

    [Fact]
    public async Task SemConfiguracaoDeVerbos_RegistraTudo()
    {
        var integracao = new LoggerIntegrationFake();

        using var host = await CriarHost(integracao, null, _ => Task.CompletedTask);

        await host.GetTestClient().SendAsync(new HttpRequestMessage(HttpMethod.Options, "/recurso"));

        Assert.Single(integracao.Registros);
    }

    /// <summary>
    /// O middleware lê o request body antes do endpoint. Sem o EnableBuffering e o
    /// rewind, o endpoint receberia um stream já consumido.
    /// </summary>
    [Fact]
    public async Task RequestBody_EhCapturadoESegueLegivelParaOEndpoint()
    {
        var integracao = new LoggerIntegrationFake();
        const string enviado = """{"nome":"teste"}""";

        using var host = await CriarHost(integracao, null, async context =>
        {
            var lido = await new StreamReader(context.Request.Body).ReadToEndAsync();
            await context.Response.WriteAsync(lido);
        });

        var resposta = await host.GetTestClient().PostAsync("/recurso", new StringContent(enviado, Encoding.UTF8, "application/json"));

        Assert.Equal(enviado, await resposta.Content.ReadAsStringAsync());
        Assert.Equal(enviado, Assert.Single(integracao.Registros).RequestBody);
    }

    [Fact]
    public async Task ResponseBody_EhCapturadoEChegaIntactoAoCliente()
    {
        var integracao = new LoggerIntegrationFake();
        const string devolvido = """{"resultado":"ok"}""";

        using var host = await CriarHost(integracao, null, async context =>
        {
            context.Response.StatusCode = (int)HttpStatusCode.Created;
            await context.Response.WriteAsync(devolvido);
        });

        var resposta = await host.GetTestClient().GetAsync("/recurso");
        var registro = Assert.Single(integracao.Registros);

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        Assert.Equal(devolvido, await resposta.Content.ReadAsStringAsync());
        Assert.Equal(devolvido, registro.ResponseBody);
        Assert.Equal((int)HttpStatusCode.Created, registro.StatusCode);
    }

    /// <summary>
    /// Resposta maior que o buffer interno do MemoryStream, para garantir que a
    /// cópia de volta não trunca nem embaralha o conteúdo.
    /// </summary>
    [Fact]
    public async Task ResponseBodyGrande_ChegaIntactoAoCliente()
    {
        var integracao = new LoggerIntegrationFake();
        var devolvido = string.Concat(Enumerable.Range(0, 20_000).Select(i => (char)('a' + i % 26)));

        using var host = await CriarHost(integracao, null, async context => await context.Response.WriteAsync(devolvido));

        var resposta = await host.GetTestClient().GetAsync("/recurso");

        Assert.Equal(devolvido, await resposta.Content.ReadAsStringAsync());
        Assert.Equal(devolvido, Assert.Single(integracao.Registros).ResponseBody);
    }

    [Fact]
    public async Task ExcecaoNoEndpoint_ViraProblemDetails500EEhRegistrada()
    {
        var integracao = new LoggerIntegrationFake();

        using var host = await CriarHost(integracao, null, _ => throw new InvalidOperationException("algo quebrou"));

        var resposta = await host.GetTestClient().GetAsync("/recurso");
        var corpo = await resposta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);

        var problema = JsonDocument.Parse(corpo).RootElement;

        Assert.Equal("algo quebrou", problema.GetProperty("detail").GetString());
        Assert.Equal(nameof(InvalidOperationException), problema.GetProperty("type").GetString());
        Assert.Equal("/recurso", problema.GetProperty("instance").GetString());

        var registro = Assert.Single(integracao.Registros);

        Assert.Equal((int)HttpStatusCode.InternalServerError, registro.StatusCode);
        Assert.Contains("algo quebrou", registro.ResponseBody);
    }

    /// <summary>
    /// Comportamento atual, documentado por ser contraintuitivo na hora de depurar:
    /// se o endpoint escreve parte da resposta e só depois estoura, o cliente recebe
    /// o ProblemDetails mas o log registra o trecho parcial descartado. Ou seja, o
    /// ResponseBody do log não é o que o cliente recebeu.
    /// </summary>
    [Fact]
    public async Task EscritaParcialSeguidaDeExcecao_LogRegistraOTrechoDescartado()
    {
        var integracao = new LoggerIntegrationFake();

        using var host = await CriarHost(integracao, null, async context =>
        {
            await context.Response.WriteAsync("parcial");

            throw new InvalidOperationException("estourou depois de escrever");
        });

        var resposta = await host.GetTestClient().GetAsync("/recurso");
        var recebidoPeloCliente = await resposta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, resposta.StatusCode);
        Assert.Contains("estourou depois de escrever", recebidoPeloCliente);
        Assert.DoesNotContain("parcial", recebidoPeloCliente);

        Assert.Equal("parcial", Assert.Single(integracao.Registros).ResponseBody);
    }

    [Fact]
    public async Task LogsCustomizados_ChegamNoRegistro()
    {
        var integracao = new LoggerIntegrationFake();

        using var host = await CriarHost(integracao, null, context =>
        {
            var servico = context.RequestServices.GetRequiredService<ILoggerActionService>();

            servico.AddLog("primeiro", "segundo");

            return Task.CompletedTask;
        });

        await host.GetTestClient().GetAsync("/recurso");

        var registro = Assert.Single(integracao.Registros);

        Assert.Equal(2, registro.Logs.Count);
        Assert.EndsWith(" - primeiro", registro.Logs[0]);
        Assert.EndsWith(" - segundo", registro.Logs[1]);
    }

    [Fact]
    public async Task DuracaoEhMedida()
    {
        var integracao = new LoggerIntegrationFake();

        using var host = await CriarHost(integracao, null, async _ => await Task.Delay(20));

        await host.GetTestClient().GetAsync("/recurso");

        var duracao = Assert.Single(integracao.Registros).DurationMiliSeconds;

        Assert.NotNull(duracao);
        Assert.True(duracao >= 20, $"Duração medida foi {duracao} ms.");
    }

    [Fact]
    public async Task IpCliente_SaiDoXForwardedForQuandoPresente()
    {
        var integracao = new LoggerIntegrationFake();

        using var host = await CriarHost(integracao, null, _ => Task.CompletedTask);

        var requisicao = new HttpRequestMessage(HttpMethod.Get, "/recurso");
        requisicao.Headers.Add("X-Forwarded-For", "203.0.113.7, 10.0.0.1");

        await host.GetTestClient().SendAsync(requisicao);

        Assert.Equal("203.0.113.7", Assert.Single(integracao.Registros).IpCliente);
    }

    private static async Task<IHost> CriarHost(
        ILoggerIntegration integracao,
        List<string>? verbosExcluidos,
        RequestDelegate endpoint)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();

                web.ConfigureServices(servicos =>
                {
                    servicos.AddSingleton(integracao);
                    servicos.AddSingleton(new LoggerActionConfiguration { VerbosHttpExcluidos = verbosExcluidos });
                    servicos.AddScoped<ILoggerActionService, LoggerActionService>();
                });

                web.Configure(app =>
                {
                    app.UseLoggerAction();
                    app.Run(endpoint);
                });
            })
            .StartAsync();

        return host;
    }

    /// <summary>
    /// Substitui o envio ao CloudWatch e guarda o que o middleware produziu.
    /// O middleware aguarda o SendLog, então a captura é determinística.
    /// </summary>
    private sealed class LoggerIntegrationFake : ILoggerIntegration
    {
        public List<LogRegister> Registros { get; } = [];

        public Task SendLog(LogRegister logRegister)
        {
            Registros.Add(logRegister);

            return Task.CompletedTask;
        }
    }
}
