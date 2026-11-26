using Microsoft.Extensions.Options;

namespace TelnetRelay;

/// <summary>Hosts the relay engine and sends its log lines to the .NET logger.</summary>
public sealed class Worker : BackgroundService
{
    private readonly RelayOptions _options;
    private readonly ILogger<Worker> _logger;

    public Worker(IOptions<RelayOptions> options, ILogger<Worker> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var engine = new RelayEngine(_options, Log);
        return engine.RunAsync(stoppingToken);
    }

    private void Log(RelayLogLevel level, string message)
    {
        switch (level)
        {
            case RelayLogLevel.Error: _logger.LogError("{Message}", message); break;
            case RelayLogLevel.Warning: _logger.LogWarning("{Message}", message); break;
            default: _logger.LogInformation("{Message}", message); break;
        }
    }
}
