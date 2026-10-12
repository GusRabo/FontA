namespace FontA.Core.Activation;

/// <summary>
/// Planning for switching many fonts on or off. Activating a font system-wide makes Windows update its font table for
/// every running app, which costs anywhere from a few milliseconds to about half a second per font depending on the
/// PC, so FontA measures the real speed, tells you how long a big job will take, and asks first when it is long.
/// </summary>
public static class ActivationPlan
{
    /// <summary>A guess used until FontA has timed a real activation on this PC.</summary>
    public const double DefaultMsPerFont = 150;

    /// <summary>Always ask before activating more fonts than this, however fast the PC is.</summary>
    public const int AlwaysAskAbove = 300;

    /// <summary>Ask when the job is expected to take at least this long (and involves more than a handful of fonts).</summary>
    public static readonly TimeSpan AskIfLongerThan = TimeSpan.FromSeconds(15);

    private const int NeverAskForFewerThan = 20;
    private const double MinMs = 1, MaxMs = 10_000;

    public static TimeSpan Estimate(int fonts, double msPerFont) =>
        TimeSpan.FromMilliseconds(Math.Max(0, fonts) * Math.Clamp(msPerFont, MinMs, MaxMs));

    public static bool NeedsConfirmation(int fonts, double msPerFont) =>
        fonts > AlwaysAskAbove || (fonts >= NeverAskForFewerThan && Estimate(fonts, msPerFont) >= AskIfLongerThan);

    /// <summary>Folds a new measurement into the running figure, trusting bigger samples more.</summary>
    public static double Blend(double previousMsPerFont, double measuredMsPerFont, int sampleFonts)
    {
        if (sampleFonts <= 0 || double.IsNaN(measuredMsPerFont) || double.IsInfinity(measuredMsPerFont)) return Clamp(previousMsPerFont);
        double weight = Math.Min(1.0, sampleFonts / 20.0);     // 20 or more fonts: the new figure counts fully
        return Clamp(previousMsPerFont * (1 - weight) + Clamp(measuredMsPerFont) * weight);
    }

    private static double Clamp(double ms) => double.IsNaN(ms) ? DefaultMsPerFont : Math.Clamp(ms, MinMs, MaxMs);

    /// <summary>A short human phrase: "a few seconds", "about 45 seconds", "about 3 minutes".</summary>
    public static string Describe(TimeSpan time)
    {
        if (time < TimeSpan.FromSeconds(5)) return "a few seconds";
        if (time < TimeSpan.FromSeconds(90)) return $"about {Math.Round(time.TotalSeconds / 5) * 5:0} seconds";
        if (time < TimeSpan.FromMinutes(90)) return $"about {Math.Round(time.TotalMinutes):0} minutes";
        return $"about {Math.Round(time.TotalHours, 1):0.#} hours";
    }
}
