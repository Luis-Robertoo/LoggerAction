using Microsoft.AspNetCore.Http;

namespace LoggerAction.Log.Domain;

public class LogRegister
{
    public Guid Id { get; protected set; }
    public string ConnectionId { get; protected set; }
    public string Path { get; protected set; }
    public string HttpMethod { get; protected set; }
    public string? ResponseBody { get; protected set; }
    public int StatusCode { get; protected set; }
    public double? DurationMiliSeconds { get; protected set; }
    public List<string> Logs { get; protected set; }

    public LogRegister(HttpContext context, double? durationMiliSeconds, string? responseBodyString, IReadOnlyList<string> logs)
    {
        Id = Guid.NewGuid();
        ConnectionId = context.Connection.Id;
        Path = $"{context.Request.Host}{context.Request.Path}{context.Request.QueryString}";
        HttpMethod = context.Request.Method;
        StatusCode = context.Response.StatusCode;
        DurationMiliSeconds = durationMiliSeconds;
        ResponseBody = responseBodyString;

        Logs = logs.ToList();
    }
}
