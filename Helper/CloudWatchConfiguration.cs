namespace LoggerAction.Log.Helper;

public class CloudWatchConfiguration
{
    public required string LogGroupName { get; set; }
    public required string LogStreamName { get; set; }
}
