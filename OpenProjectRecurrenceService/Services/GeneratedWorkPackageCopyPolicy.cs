namespace OpenProjectRecurrenceService;

public sealed class GeneratedWorkPackageCopyPolicy
{
    public GeneratedWorkPackage Build(WorkPackageTemplate source, DateOnly scheduledOccurrence, int generatedTypeId)
    {
        var subject = BuildSubject(source.Subject, scheduledOccurrence);
        var dueDate = scheduledOccurrence.AddDays(source.DueAfter);

        return new GeneratedWorkPackage
        {
            ProjectId = source.ProjectId,
            TypeId = generatedTypeId,
            ParentId = source.Id,
            Subject = subject,
            Description = source.Description,
            DescriptionFormat = source.DescriptionFormat,
            AssigneeId = source.AssigneeId,
            PriorityId = source.PriorityId,
            CategoryId = source.CategoryId,
            StartDate = scheduledOccurrence,
            DueDate = dueDate,
            RecurrenceEnabled = false,
            Relations = source.Relations
                .Where(x => !string.Equals(x.Type, "parent", StringComparison.OrdinalIgnoreCase))
                .Select(x => new RelationLink { Type = x.Type, Id = x.Id, Name = x.Name })
                .ToList(),
            CustomFields = source.CustomFields
                .Where(x => !string.Equals(x.Key, "Recurrence enabled", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static string BuildSubject(string sourceSubject, DateOnly scheduledOccurrence)
    {
        var template = string.IsNullOrWhiteSpace(sourceSubject)
            ? "{SourceSubject} - {yyyy-MM}"
            : "{SourceSubject} - {yyyy-MM}";

        var formatted = template
            .Replace("{SourceSubject}", sourceSubject, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", scheduledOccurrence.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{yyyy-MM}", scheduledOccurrence.ToString("yyyy-MM"), StringComparison.OrdinalIgnoreCase)
            .Replace("{yyyy}", scheduledOccurrence.Year.ToString(), StringComparison.OrdinalIgnoreCase);

        return string.IsNullOrWhiteSpace(formatted) ? sourceSubject : formatted;
    }
}

public sealed class GeneratedWorkPackage
{
    public int ProjectId { get; set; }

    public int TypeId { get; set; }

    public int ParentId { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string DescriptionFormat { get; set; } = "markdown";

    public int? AssigneeId { get; set; }

    public int? PriorityId { get; set; }

    public int? CategoryId { get; set; }

    public DateOnly StartDate { get; set; }

    public DateOnly DueDate { get; set; }

    public bool RecurrenceEnabled { get; set; }

    public List<RelationLink> Relations { get; set; } = [];

    public Dictionary<string, string?> CustomFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
