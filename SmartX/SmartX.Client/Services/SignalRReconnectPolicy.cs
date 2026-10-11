using Microsoft.AspNetCore.SignalR.Client;

namespace SmartX.Client.Services;

public sealed class SignalRReconnectPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        var delaySeconds = retryContext.PreviousRetryCount switch
        {
            <= 0 => 1,
            1 => 2,
            2 => 4,
            3 => 8,
            4 => 16,
            _ => 30
        };
        return TimeSpan.FromSeconds(delaySeconds);
    }
}
