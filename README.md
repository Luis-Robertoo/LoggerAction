# LoggerAction

A .NET middleware library for logging HTTP requests and responses to AWS CloudWatch. Supports both API and Worker/BackgroundJob contexts.

---

## Installation

```bash
dotnet add package LoggerAction.Log
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
  },
  "LoggerActionConfiguration": {
    "VerbosHttpExcluidos": [ "OPTIONS" ]
  }
}
```

`AWSConfiguration` and `CloudWatchConfiguration` are required — `AddLoggerAction` throws `InvalidOperationException` at startup if either section is missing, instead of failing silently later.

### Excluding HTTP methods

`LoggerActionConfiguration.VerbosHttpExcluidos` is optional. Any request whose method is listed is skipped by the middleware **before** any work is done — no request body buffering, no response interception, no serialization. Useful for CORS preflight (`OPTIONS`) and health checks, which otherwise pollute the log group and add ingestion cost.

Matching is case-insensitive, so `"OPTIONS"`, `"Options"` and `"options"` all work.

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
| `DurationMiliSeconds` | Total request duration in milliseconds |
| `IpCliente` | Client IP address (resolved from `X-Forwarded-For` when behind a proxy) |
| `TipoProcessamento` | `"API"` for HTTP requests, `"Worker/Jobs"` for background services |
| `Logs` | List of custom log messages added via `ILoggerActionService` |

---

## Delivery to CloudWatch

Logs are **not** sent inline with the request. `ILoggerIntegration.SendLog` starts the delivery on a background task and returns immediately, so neither JSON serialization nor SigV4 signing happens on the request thread — the response is not held waiting for CloudWatch.

Each record is sent with its own `PutLogEvents` call, retried up to **3 times** with exponential backoff (2s, then 4s). Errors that retrying cannot fix (`ResourceNotFoundException`, `InvalidParameterException`, `DataAlreadyAcceptedException`) are not retried. After the last attempt the record is discarded and the failure is written to the console with the `-- LoggerAction ERROR --` prefix, so a misconfigured log group or bad credentials is visible instead of silent.

### Size limit

CloudWatch rejects any single event above **256 KB**, and it rejects the whole event — not just the excess. To keep the record from being lost, oversized entries are trimmed before sending, in this order:

1. `RequestBody` and `ResponseBody` are truncated, with `...[truncado pelo LoggerAction]` appended. The available space is split between them; a body that already fits in half is kept whole and the leftover goes to the larger one.
2. If the record still does not fit without the bodies, the `Logs` list is cut down and a final entry states how many messages were dropped.

Every trim prints a `-- LoggerAction WARN --` line identifying the record.

**Known limits:**

- **No concurrency cap.** Each request starts its own delivery task. If CloudWatch gets slow, the retry delays make pending tasks accumulate without a ceiling.
- **No batching.** One `PutLogEvents` per record. This does not add cost — CloudWatch Logs bills ingested volume, not API calls — but it counts against the per-account throttling quota.
- **Loss on shutdown.** Deliveries in flight are dropped when the process exits.
- **Loss after 3 attempts.** The record is discarded and reported on the console.

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