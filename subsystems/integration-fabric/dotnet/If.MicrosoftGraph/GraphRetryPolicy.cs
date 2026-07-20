using Microsoft.Graph.Models.ODataErrors;

namespace If.MicrosoftGraph;

/// <summary>Retry policy for Microsoft Graph HTTP calls — handles 429 throttling with exponential backoff.</summary>
public sealed class GraphRetryPolicy
{
    /// <summary>
    /// Executes the operation, retrying on 429 (Too Many Requests) with exponential backoff.
    /// Respects <paramref name="ct"/>. Non-429 exceptions propagate immediately.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> operation,
        CancellationToken ct,
        int maxAttempts = 3)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (ODataError ex) when (ex.ResponseStatusCode == 429 && attempt < maxAttempts - 1)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                await Task.Delay(delay, ct);
            }
        }
    }
}
