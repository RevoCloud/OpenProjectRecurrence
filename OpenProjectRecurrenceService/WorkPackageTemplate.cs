using System.Text.Json;

namespace OpenProjectRecurrenceService;

public sealed class WorkPackageTemplate
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string DescriptionFormat { get; set; } = "markdown";

    public int? AssigneeId { get; set; }

    public int? PriorityId { get; set; }

    public int? CategoryId { get; set; }

    public int? TypeId { get; set; }

    public bool RecurrenceEnabled { get; set; }

    public string GeneratedTypeName { get; set; } = string.Empty;

    public RecurrenceType RecurrenceType { get; set; }

    public int Interval { get; set; } = 1;

    public DateOnly? NextOccurrence { get; set; }

    public int GenerateAhead { get; set; } = 0;

    public int DueAfter { get; set; } = 0;

    public List<RelationLink> Relations { get; set; } = [];

    public Dictionary<string, string?> CustomFields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, OpenProjectSchemaCustomFieldDefinition> CustomFieldDefinitionsByProperty { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, JsonElement> CustomFieldRawValuesByProperty { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> CustomFieldLinkHrefsByProperty { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RelationLink
{
    public string Type { get; set; } = string.Empty;

    public int Id { get; set; }

    public string? Name { get; set; }
}
