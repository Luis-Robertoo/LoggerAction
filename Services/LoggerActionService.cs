using LoggerAction.Log.Interfaces;

namespace LoggerAction.Log.Services;

public class LoggerActionService : ILoggerActionService
{
    private List<string> _logs = new List<string>();

    public IReadOnlyList<string> Logs => _logs;

    public void AddLog(params string[] logs)
    {
        for (int i = 0; i < logs.Length; i++)
        {
            _logs.Add($"{DateTime.UtcNow.ToString("dd/MM/yyyy HH:mm:ss:fff")} - {logs[i]}");
        }
    }
}
