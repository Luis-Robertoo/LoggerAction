using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using LoggerAction.Log.Domain;
using LoggerAction.Log.Helper;
using LoggerAction.Log.Interfaces;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace LoggerAction.Log.Services;

public class LoggerIntegration(IAmazonCloudWatchLogs amazonCloudWatchLogs, IConfiguration configuration) : ILoggerIntegration
{
    private readonly CloudWatchConfiguration _awsCloudWatchConfig = configuration.GetSection(nameof(CloudWatchConfiguration)).Get<CloudWatchConfiguration>();

    public async Task SendLog(LogRegister logRegister)
    {
        try
        {
            var logEvents = new List<InputLogEvent>
            {
                new InputLogEvent
                {
                    Message = JsonSerializer.Serialize(logRegister),
                    Timestamp = DateTime.UtcNow
                }
            };

            amazonCloudWatchLogs
                .PutLogEventsAsync(new PutLogEventsRequest(_awsCloudWatchConfig.LogGroupName, _awsCloudWatchConfig.LogStreamName, logEvents));
        }
        catch (Exception ex)
        {

            Console.WriteLine($"-- LoggerAction ERROR -- Message: {ex.Message} ### Stack Trace: {ex.StackTrace}");
        }
    }
}
