using Microsoft.AspNetCore.SignalR;
using SmartX.Api.Hubs;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Simple randomized mock telemetry generator (per the assignment's minimum
/// seeding requirement, no physics modelling). Every tick it walks the
/// registered devices, has a small chance of skipping (variable reporting
/// rate), a small chance of a spike, and a small chance of a disconnect, and
/// pushes the resulting DeviceStatus to every connected dashboard over
/// TelemetryHub.
/// </summary>
public sealed class TelemetrySeeder(SensorRegistry registry, IHubContext<TelemetryHub> hub, ILogger<TelemetrySeeder> logger)
    : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);
    private const int TicksPerThroughputWindow = 30; // ~60s at a 2s tick

    private readonly Random _random = new();
    private bool _isRunning = true;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private CancellationTokenSource? _stoppingTokenSource;

    public bool IsRunning
    {
        get { lock (this) { return _isRunning; } }
    }

    public async Task StopSeedingAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            if (!_isRunning) return;
            _isRunning = false;
            _stoppingTokenSource?.Cancel();
            logger.LogInformation("Telemetry seeder stopped.");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task StartSeedingAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            if (_isRunning) return;
            _isRunning = true;
            _stoppingTokenSource = new CancellationTokenSource();
            // Restart the background service
            _ = Task.Run(() => ExecuteAsync(_stoppingTokenSource.Token));
            logger.LogInformation("Telemetry seeder started.");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // Public sync methods for backward compatibility
    public void StopSeeding()
    {
        _ = StopSeedingAsync();
    }

    public void StartSeeding()
    {
        _ = StartSeedingAsync();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var devices = registry.DeviceNodes.ToList();
        var currentValue = devices.ToDictionary(d => d.Id, d => InitialValueFor(d.Sensor!.Category));

        logger.LogInformation("Telemetry seeder starting for {Count} devices.", devices.Count);

        var tick = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Check if we should be running
            bool shouldRun;
            await _semaphore.WaitAsync();
            try
            {
                shouldRun = _isRunning;
            }
            finally
            {
                _semaphore.Release();
            }

            if (!shouldRun)
            {
                await Task.Delay(100, stoppingToken);
                continue;
            }

            foreach (var device in devices)
            {
                if (_random.NextDouble() < 0.05) continue; // simulate a device that didn't report this tick

                var category = device.Sensor!.Category;
                var isDisconnect = _random.NextDouble() < 0.01;
                var isSpike = !isDisconnect && _random.NextDouble() < 0.03;

                float value;
                if (isDisconnect)
                {
                    value = currentValue[device.Id];
                }
                else if (isSpike)
                {
                    value = currentValue[device.Id] * (float)(1.8 + _random.NextDouble());
                }
                else
                {
                    var noise = (float)((_random.NextDouble() - 0.5) * 2 * NoiseFor(category));
                    value = currentValue[device.Id] + noise;
                    currentValue[device.Id] = value;
                }

                var status = registry.RecordReading(device.Id, value, simulateDisconnect: isDisconnect);
                if (status is not null)
                {
                    await hub.Clients.All.SendAsync("DeviceStatusUpdated", status, stoppingToken);
                }
            }

            tick++;
            if (tick % TicksPerThroughputWindow == 0)
            {
                registry.ResetThroughputWindow();
            }

            await Task.Delay(TickInterval, stoppingToken);
        }
    }

    private static float InitialValueFor(SensorCategory category) => category switch
    {
        SensorCategory.Environmental => 22f,
        SensorCategory.Power => 220f,
        SensorCategory.Actuator => 1f,
        _ => 0f
    };

    private static float NoiseFor(SensorCategory category) => category switch
    {
        SensorCategory.Environmental => 1.2f,
        SensorCategory.Power => 15f,
        SensorCategory.Actuator => 0.05f,
        _ => 1f
    };
}