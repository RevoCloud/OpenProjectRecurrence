using Microsoft.Extensions.Configuration;

namespace OpenProjectRecurrenceService;

public sealed class OpenProjectOptions
{
    private string _apiKey = string.Empty;

    public const string SectionName = "OpenProject";

    public string BaseUrl { get; set; } = "https://openproject.example.com";

    public string ApiToken { get; set; } = string.Empty;

    [ConfigurationKeyName("apiKey")]
    public string ApiKey
    {
        get => string.IsNullOrWhiteSpace(ApiToken) ? _apiKey : ApiToken;
        set => _apiKey = value;
    }

    public string EffectiveApiToken => string.IsNullOrWhiteSpace(ApiToken) ? _apiKey : ApiToken;

    public List<int> ProjectIds { get; set; } = [];

    public List<string> ProjectNames { get; set; } = ["RevoCloud ISMS"];

    public int PollingIntervalMinutes { get; set; } = 15;

    public int MaximumCatchUpOccurrencesPerTemplatePerRun { get; set; } = 12;

    public CustomFieldConfiguration CustomFields { get; set; } = new();
}

public sealed class CustomFieldConfiguration
{
    public string RecurrenceEnabled { get; set; } = "Recurrence enabled";

    public string GeneratedType { get; set; } = "Generated type";

    public string RecurrenceType { get; set; } = "Recurrence type";

    public string Interval { get; set; } = "Interval";

    public string NextOccurrence { get; set; } = "Next occurrence";

    public string GenerateAhead { get; set; } = "Generate ahead";

    public string DueAfter { get; set; } = "Due after";
}
