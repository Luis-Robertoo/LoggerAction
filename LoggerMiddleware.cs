using LoggerAction.Log.Domain;
using LoggerAction.Log.Helper;
using LoggerAction.Log.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace LoggerAction.Log;

public class LoggerMiddleware(RequestDelegate next, ILoggerIntegration loggerIntegration, LoggerActionConfiguration configuracao)
{
    private readonly HashSet<string> _verbosExcluidos =
        new(configuracao.VerbosHttpExcluidos ?? [], StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _pathExcluidos =
    new(configuracao.PathExcluidos ?? [], StringComparer.OrdinalIgnoreCase);


    public async Task InvokeAsync(HttpContext context, ILoggerActionService loggerActionService)
    {
        // Curto-circuito antes de qualquer buffering: verbos excluídos não pagam
        // o custo de ler o request body nem de interceptar o response.
        if (_verbosExcluidos.Contains(context.Request.Method))
        {
            await next(context);
            return;
        }

        var requestBodyText = string.Empty;
        try
        {
            context.Request.EnableBuffering();
            requestBodyText = await new StreamReader(context.Request.Body).ReadToEndAsync();
            context.Request.Body.Position = 0;

            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var originalResponseBody = context.Response.Body;
            using var memStream = new MemoryStream();
            context.Response.Body = memStream;
            ProblemDetails? problemDetails = null;

            try
            {
                await next(context);
                memStream.Position = 0;

                await memStream.CopyToAsync(originalResponseBody);
                context.Response.Body = originalResponseBody;
            }
            catch (Exception ex)
            {
                context.Response.Body = originalResponseBody;

                var statusCode = (int)HttpStatusCode.InternalServerError;

                problemDetails = new ProblemDetails
                {
                    Title = $"Ocorreu um erro ao processar a requisição.",
                    Status = statusCode,
                    Type = ex.GetBaseException().GetType().Name,
                    Detail = ex.Message,
                    Instance = context.Request.Path
                };

                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json";

                await context.Response.WriteAsync(JsonSerializer.Serialize(problemDetails));
            }

            stopwatch.Stop();

            memStream.Position = 0;
            var responseBodyText = await new StreamReader(memStream).ReadToEndAsync();
            if (problemDetails != null && memStream.Length == 0)
            {
                responseBodyText = JsonSerializer.Serialize(problemDetails);
            }

            await loggerIntegration.SendLog(new LogRegister(context, stopwatch.Elapsed.TotalMilliseconds, responseBodyText, requestBodyText, loggerActionService.Logs));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"-- LoggerAction ERROR -- Falha no middleware. Message: {ex.Message} ### Stack Trace: {ex.StackTrace}");
            await loggerIntegration.SendLog(new LogRegister(context, null, null, requestBodyText, loggerActionService.Logs));
        }
    }
}
