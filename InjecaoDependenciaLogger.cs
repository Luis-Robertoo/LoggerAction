using Amazon;
using Amazon.CloudWatchLogs;
using Amazon.Extensions.NETCore.Setup;
using Amazon.Runtime;
using LoggerAction.Log.Helper;
using LoggerAction.Log.Interfaces;
using LoggerAction.Log.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LoggerAction.Log;

public static class InjecaoDependenciaLogger
{
    public static void AddLoggerAction(this IServiceCollection services, IConfiguration configuration)
    {
        var awsConfig = configuration.GetSection(nameof(AWSConfiguration)).Get<AWSConfiguration>()
            ?? throw new InvalidOperationException($"LoggerAction: seção '{nameof(AWSConfiguration)}' não encontrada na configuração.");

        var cloudWatchConfig = configuration.GetSection(nameof(CloudWatchConfiguration)).Get<CloudWatchConfiguration>()
            ?? throw new InvalidOperationException($"LoggerAction: seção '{nameof(CloudWatchConfiguration)}' não encontrada na configuração.");

        var loggerActionConfig = configuration.GetSection(nameof(LoggerActionConfiguration)).Get<LoggerActionConfiguration>()
            ?? new LoggerActionConfiguration();

        services.AddSingleton(cloudWatchConfig);
        services.AddSingleton(loggerActionConfig);

        services.AddScoped<ILoggerActionService, LoggerActionService>();
        services.AddSingleton<ILoggerIntegration, LoggerIntegration>();

        var options = new AWSOptions
        {
            Region = RegionEndpoint.GetBySystemName(awsConfig.Region),
            Credentials  = new BasicAWSCredentials(awsConfig.AccessKey, awsConfig.SecretKey)
        };

        services.AddAWSService<IAmazonCloudWatchLogs>(options);
    }

    public static IApplicationBuilder UseLoggerAction(this IApplicationBuilder builder)
    {
        builder.UseMiddleware<LoggerMiddleware>();
    
        return builder;
    }
}
