using Crawler_project.Checks;

public sealed class DnsblOptions
{
    public int DnsTimeoutMs { get; set; } = 2000;
    public int MaxConcurrentQueries { get; set; } = 8;

    // Минимальный стартовый набор. ВАЖНО: проверь условия использования каждой зоны.
    public List<DnsblZone> Zones { get; } = new()
    {
        new("Spamhaus ZEN", "zen.spamhaus.org", IssueSeverity.Warning),
        new("SpamCop BL", "bl.spamcop.net", IssueSeverity.Warning),
        new("Barracuda BRBL", "b.barracudacentral.org", IssueSeverity.Warning),
    };
}

public sealed record DnsblZone(string Name, string Zone, IssueSeverity Severity);