namespace BootWakeMailer.Tests;

/// <summary>
/// Polling helper for the few tests that observe a background loop, so a test never
/// depends on a fixed sleep being long enough.
/// </summary>
internal static class Wait
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Returns as soon as <paramref name="condition"/> is true.</summary>
    /// <exception cref="Xunit.Sdk.XunitException">The condition was still false at the timeout.</exception>
    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(15).ConfigureAwait(false);
        }

        Assert.Fail($"Condition was not met within {timeout ?? DefaultTimeout}.");
    }
}
