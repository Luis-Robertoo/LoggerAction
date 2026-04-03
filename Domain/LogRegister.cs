using Microsoft.AspNetCore.Http;

namespace LoggerAction.Log.Domain;

public class LogRegister
{
    public Guid Id { get; protected set; }
    public string? ConnectionId { get; protected set; }
    public string? Path { get; protected set; }
    public string? HttpMethod { get; protected set; }
    public string? ResponseBody { get; protected set; }
    public string? RequestBody { get; protected set; }
    public int? StatusCode { get; protected set; }
    public double? DurationMiliSeconds { get; protected set; }
    public string? IpCliente { get; protected set; }
    public string TipoProcessamento { get; protected set; }
    public List<string> Logs { get; protected set; }

    public LogRegister(HttpContext context, double? durationMiliSeconds, string? responseBodyString, string? requestBodyString, IReadOnlyList<string> logs)
    {
        Id = Guid.NewGuid();
        ConnectionId = context.Connection.Id;
        Path = $"{context.Request.Host}{context.Request.Path}{context.Request.QueryString}";
        HttpMethod = context.Request.Method;
        StatusCode = context.Response.StatusCode;
        DurationMiliSeconds = durationMiliSeconds;
        ResponseBody = responseBodyString;
        RequestBody = requestBodyString;
        IpCliente = GetClientIp(context);

        TipoProcessamento = "API";
        Logs = logs.ToList();
    }

    public LogRegister(IReadOnlyList<string> logs)
    {
        Id = Guid.NewGuid();
        TipoProcessamento = "Worker/Jobs";
        Logs = logs.ToList();
    }

    private string GetClientIp(HttpContext context)
    {
        // tenta pegar do X-Forwarded-For primeiro
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();

        if (!string.IsNullOrEmpty(forwardedFor))
        {
            // pode vir múltiplos IPs separados por vírgula:
            // "clienteIP, proxy1IP, proxy2IP"
            // o primeiro sempre é o cliente real
            return forwardedFor.Split(',')[0].Trim();
        }

        // fallback: IP direto da conexão (sem proxy)
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
