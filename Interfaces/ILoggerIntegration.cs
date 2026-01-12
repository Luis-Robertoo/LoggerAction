using LoggerAction.Log.Domain;

namespace LoggerAction.Log.Interfaces;

public interface ILoggerIntegration
{
    Task SendLog(LogRegister logRegister);
}
