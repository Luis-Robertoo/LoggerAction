namespace LoggerAction.Log.Interfaces;

public interface ILoggerActionService
{
    IReadOnlyList<string> Logs { get; }
    void AddLog(params string[] log);
}
