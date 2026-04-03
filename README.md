# LoggerAction

A .NET middleware library for logging HTTP requests and responses to AWS CloudWatch. Supports both API and Worker/BackgroundJob contexts.

---

## Installation

```bash
dotnet add package LoggerAction
```

---

## Requirements

- .NET 8+
- AWS Account with CloudWatch access
- AWS credentials with `logs:PutLogEvents` and `logs:CreateLogStream` permissions

---

## Configuration

Add the following to your `appsettings.json`:

```json
{
  "AWSConfiguration": {
    "AccessKey": "YOUR_ACCESS_KEY",
    "SecretKey": "YOUR_SECRET_KEY",
    "Region": "us-east-1"
  },
  "CloudWatchConfiguration": {
    "LogGroupName": "your-log-group-name",
    "LogStreamName": "your-log-stream-name"
  }
}
```

---

## Usage

### API

Register and use the middleware in `Program.cs`:

```csharp
builder.Services.AddLoggerAction(builder.Configuration);

app.UseLoggerAction();
```

The middleware will automatically:
- Capture the request body and client IP (including proxy forwarding via `X-Forwarded-For`)
- Capture the response body and status code
- Measure request duration in milliseconds
- Handle and log unhandled exceptions as `ProblemDetails`
- Send all logs to CloudWatch

#### Adding custom logs inside a request

Inject `ILoggerActionService` anywhere in your application to attach custom logs to the current request:

```csharp
public class MyService(ILoggerActionService loggerActionService)
{
    public void DoSomething()
    {
        loggerActionService.AddLog("Custom log message");
        loggerActionService.AddLog("Step 1 completed", "Step 2 completed");
    }
}
```

Each log entry is automatically timestamped in `dd/MM/yyyy HH:mm:ss:fff` format.

---

### Worker / BackgroundJob

For background services, resolve `ILoggerActionService` from a scoped service and send the log manually at the end of each execution cycle:

```csharp
public class MyBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILoggerIntegration loggerIntegration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            using var scope = scopeFactory.CreateScope();
            var loggerActionService = scope.ServiceProvider.GetRequiredService<ILoggerActionService>();

            try
            {
                loggerActionService.AddLog("Starting job execution");

                // your logic here

                loggerActionService.AddLog("Job completed successfully");
            }
            catch (Exception ex)
            {
                loggerActionService.AddLog($"Error: {ex.Message}", $"StackTrace: {ex.StackTrace}");
            }
            finally
            {
                await loggerIntegration.SendLog(new LogRegister(loggerActionService.Logs));
            }
        }
    }
}
```

> **Important:** Always resolve `ILoggerActionService` from a new scope per execution cycle. Injecting it directly into the constructor of a `BackgroundService` will cause a runtime error since it is registered as `Scoped`.

---

## Log Structure

Each log entry sent to CloudWatch contains:

| Field | Description |
|---|---|
| `Id` | Unique identifier (GUID) for the log entry |
| `ConnectionId` | ASP.NET connection ID |
| `Path` | Full request path including host and query string |
| `HttpMethod` | HTTP method (GET, POST, etc.) |
| `StatusCode` | HTTP response status code |
| `RequestBody` | Raw request body |
| `ResponseBody` | Raw response body |
| `DurationMilliseconds` | Total request duration in milliseconds |
| `IpCliente` | Client IP address (resolved from `X-Forwarded-For` when behind a proxy) |
| `TipoProcessamento` | `"API"` for HTTP requests, `"Worker/Jobs"` for background services |
| `Logs` | List of custom log messages added via `ILoggerActionService` |

---

## Client IP Resolution

When the API is behind a proxy, load balancer, or API Gateway, the client IP is resolved from the `X-Forwarded-For` header:

```
X-Forwarded-For: clientIP, proxy1IP, proxy2IP
```

The first value is always used as the real client IP. If the header is not present, the connection's `RemoteIpAddress` is used as a fallback.

---

## Error Handling

Unhandled exceptions thrown during request processing are caught by the middleware and returned as a standardized `ProblemDetails` response:

```json
{
  "title": "Ocorreu um erro ao processar a requisição.",
  "status": 500,
  "type": "ExceptionTypeName",
  "detail": "Exception message",
  "instance": "/your/path"
}
```

The exception is also logged to CloudWatch with the full context of the request.