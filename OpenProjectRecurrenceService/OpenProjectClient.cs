using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace OpenProjectRecurrenceService;

public sealed class OpenProjectClient(
    HttpClient httpClient,
    IOptions<OpenProjectOptions> options,
    ILogger<OpenProjectClient> logger)
{
    private readonly string _baseUrl = options.Value.BaseUrl.TrimEnd('/');
    private readonly string _apiToken = options.Value.EffectiveApiToken;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<List<WorkPackageTemplate>> GetTemplatesAsync(CancellationToken cancellationToken)
    {
        var projectIds = await GetConfiguredProjectIdsAsync(cancellationToken);
        if (projectIds.Count == 0)
        {
            logger.LogInformation("No OpenProject projects configured for recurrence processing.");
            return [];
        }

        var schemaCache = new Dictionary<string, Dictionary<string, OpenProjectSchemaCustomFieldDefinition>>(StringComparer.OrdinalIgnoreCase);
        var configuredRequiredFields = GetRequiredCustomFieldNames().ToList();
        var knownFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var templates = new List<WorkPackageTemplate>();

        foreach (var projectId in projectIds)
        {
            var endpoint = $"{_baseUrl}/api/v3/projects/{projectId}/work_packages?pageSize=100";
            var request = CreateRequest(HttpMethod.Get, endpoint);

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Unable to load work packages for project {projectId}. Status: {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var result = await response.Content.ReadFromJsonAsync<OpenProjectWorkPackageList>(_jsonOptions, cancellationToken);
            var items = result?._Embedded?.Elements ?? [];

            foreach (var item in items)
            {
                var schemaHref = item.Links?.Schema?.Href;
                if (string.IsNullOrWhiteSpace(schemaHref))
                {
                    logger.LogWarning("Work package {WorkPackageId} has no schema link and will be skipped.", item.Id);
                    continue;
                }

                var schemaFieldMap = await GetSchemaCustomFieldMapAsync(schemaHref, schemaCache, cancellationToken);
                foreach (var definition in schemaFieldMap.Values)
                {
                    knownFieldNames.Add(definition.Name);
                }

                var customFieldValues = ReadCustomFieldValues(item, schemaFieldMap);
                var template = MapWorkPackage(item, projectId, schemaFieldMap, customFieldValues);
                if (template is not null)
                {
                    templates.Add(template);
                }
            }
        }

        var missingRequiredFields = configuredRequiredFields
            .Where(requiredField => !knownFieldNames.Contains(requiredField))
            .ToList();
        if (missingRequiredFields.Count > 0)
        {
            throw new InvalidOperationException($"Required custom field definitions are missing from OpenProject: {string.Join(", ", missingRequiredFields)}.");
        }

        return templates;
    }

    public async Task<List<int>> GetConfiguredProjectIdsAsync(CancellationToken cancellationToken)
    {
        var configuredProjectIds = options.Value.ProjectIds;
        if (configuredProjectIds.Count > 0)
        {
            return configuredProjectIds;
        }

        var projectNames = options.Value.ProjectNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (projectNames.Count == 0)
        {
            return [];
        }

        var endpoint = $"{_baseUrl}/api/v3/projects?limit=100";
        var request = CreateRequest(HttpMethod.Get, endpoint);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Unable to resolve configured OpenProject project names. Status: {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<OpenProjectProjectCollection>(_jsonOptions, cancellationToken);
        var projects = payload?._Embedded?.Elements ?? [];
        var results = new List<int>();

        foreach (var configuredName in projectNames)
        {
            var match = projects.FirstOrDefault(x => string.Equals(x.Name, configuredName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                logger.LogWarning("Configured OpenProject project name '{ProjectName}' was not found. It will be ignored.", configuredName);
                continue;
            }

            results.Add(match.Id);
        }

        return results;
    }

    public async Task<int> ResolveGeneratedTypeIdAsync(string typeName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw new InvalidOperationException("Generated type name must be configured.");
        }

        var endpoint = $"{_baseUrl}/api/v3/types?filters=[{{\"name\":{{\"operator\":\"=\",\"values\":[\"{EscapeJson(typeName)}\"]}}}}]";
        var request = CreateRequest(HttpMethod.Get, endpoint);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Unable to resolve generated work package type '{typeName}'. Status: {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<OpenProjectTypeList>(_jsonOptions, cancellationToken);
        var type = payload?._Embedded?.Elements.FirstOrDefault(x => string.Equals(x.Name, typeName, StringComparison.OrdinalIgnoreCase));
        if (type is null)
        {
            throw new InvalidOperationException($"OpenProject type '{typeName}' was not found and cannot be used as the generated type.");
        }

        return type.Id;
    }

    public async Task<int> CreateGeneratedWorkPackageAsync(WorkPackageTemplate sourceTemplate, GeneratedWorkPackage generatedWorkPackage, CancellationToken cancellationToken)
    {
        var endpoint = $"{_baseUrl}/api/v3/workspaces/{generatedWorkPackage.ProjectId}/work_packages";
        var request = CreateRequest(HttpMethod.Post, endpoint);
        var payload = JsonSerializer.Serialize(
            await BuildCreatePayloadAsync(sourceTemplate, generatedWorkPackage, cancellationToken),
            _jsonOptions);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"OpenProject rejected the generated work package. Status: {(int)response.StatusCode} {response.ReasonPhrase}. Response: {responseBody}");
        }

        var created = await response.Content.ReadFromJsonAsync<OpenProjectCreatedResource>(_jsonOptions, cancellationToken);
        return created?.Id ?? 0;
    }

    public async Task UpdateNextOccurrenceAsync(int sourceWorkPackageId, DateOnly expectedValue, CancellationToken cancellationToken)
    {
        var sourceWorkPackage = await GetWorkPackageAsync(sourceWorkPackageId, cancellationToken);
        var schemaHref = sourceWorkPackage.Links?.Schema?.Href;
        if (string.IsNullOrWhiteSpace(schemaHref))
        {
            throw new InvalidOperationException($"Unable to update Next occurrence for work package {sourceWorkPackageId} because no schema link is present.");
        }

        var schemaMap = await GetSchemaCustomFieldMapAsync(schemaHref, new Dictionary<string, Dictionary<string, OpenProjectSchemaCustomFieldDefinition>>(StringComparer.OrdinalIgnoreCase), cancellationToken);
        var propertyName = FindCustomFieldPropertyName(schemaMap, options.Value.CustomFields.NextOccurrence);
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            throw new InvalidOperationException($"Unable to update Next occurrence for work package {sourceWorkPackageId} because custom field '{options.Value.CustomFields.NextOccurrence}' is not present in schema {schemaHref}.");
        }

        var endpoint = $"{_baseUrl}/api/v3/work_packages/{sourceWorkPackageId}";
        var request = CreateRequest(HttpMethod.Patch, endpoint);
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["lockVersion"] = sourceWorkPackage.LockVersion,
            [propertyName] = expectedValue.ToString("yyyy-MM-dd")
        }, _jsonOptions);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Unable to update Next occurrence for work package {sourceWorkPackageId}. Status: {(int)response.StatusCode} {response.ReasonPhrase}. Response: {responseBody}");
        }
    }

    public async Task<bool> VerifyUpdatedNextOccurrenceAsync(int sourceWorkPackageId, DateOnly expectedValue, CancellationToken cancellationToken)
    {
        var sourceWorkPackage = await GetWorkPackageAsync(sourceWorkPackageId, cancellationToken);
        var schemaHref = sourceWorkPackage.Links?.Schema?.Href;
        if (string.IsNullOrWhiteSpace(schemaHref))
        {
            throw new InvalidOperationException($"Unable to verify Next occurrence for work package {sourceWorkPackageId} because no schema link is present.");
        }

        var schemaMap = await GetSchemaCustomFieldMapAsync(schemaHref, new Dictionary<string, Dictionary<string, OpenProjectSchemaCustomFieldDefinition>>(StringComparer.OrdinalIgnoreCase), cancellationToken);
        var values = ReadCustomFieldValues(sourceWorkPackage, schemaMap);
        return values.TryGetValue(options.Value.CustomFields.NextOccurrence, out var actualValue)
               && string.Equals(actualValue, expectedValue.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<OpenProjectWorkPackageItem> GetWorkPackageAsync(int workPackageId, CancellationToken cancellationToken)
    {
        var endpoint = $"{_baseUrl}/api/v3/work_packages/{workPackageId}";
        var request = CreateRequest(HttpMethod.Get, endpoint);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Unable to read work package {workPackageId}. Status: {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<OpenProjectWorkPackageItem>(_jsonOptions, cancellationToken);
        if (payload is null)
        {
            throw new InvalidOperationException($"OpenProject returned an empty response for work package {workPackageId}.");
        }

        return payload;
    }

    private async Task<Dictionary<string, OpenProjectSchemaCustomFieldDefinition>> GetSchemaCustomFieldMapAsync(
        string schemaHref,
        Dictionary<string, Dictionary<string, OpenProjectSchemaCustomFieldDefinition>> schemaCache,
        CancellationToken cancellationToken)
    {
        if (schemaCache.TryGetValue(schemaHref, out var cached))
        {
            return cached;
        }

        var endpoint = schemaHref.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? schemaHref
            : $"{_baseUrl}{schemaHref}";
        var request = CreateRequest(HttpMethod.Get, endpoint);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Unable to read schema '{schemaHref}'. Status: {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        var result = new Dictionary<string, OpenProjectSchemaCustomFieldDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (!property.Name.StartsWith("customField", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!property.Value.TryGetProperty("name", out var nameProperty) || nameProperty.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var fieldName = nameProperty.GetString();
            if (!string.IsNullOrWhiteSpace(fieldName))
            {
                result[property.Name] = new OpenProjectSchemaCustomFieldDefinition(
                    Id: ParseCustomFieldId(property.Name),
                    PropertyName: property.Name,
                    Name: fieldName);
            }
        }

        schemaCache[schemaHref] = result;
        return result;
    }

    private static Dictionary<string, string?> ReadCustomFieldValues(OpenProjectWorkPackageItem item, Dictionary<string, OpenProjectSchemaCustomFieldDefinition> schemaFieldMap)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (propertyName, definition) in schemaFieldMap)
        {
            var resolved = ResolveCustomFieldValue(item, propertyName);
            if (resolved is not null)
            {
                result[definition.Name] = resolved;
            }
        }

        return result;
    }

    private static string? ResolveCustomFieldValue(OpenProjectWorkPackageItem item, string propertyName)
    {
        if (item.ExtensionData is not null
            && item.ExtensionData.TryGetValue(propertyName, out var rawValue))
        {
            var direct = ReadJsonValue(rawValue);
            if (!string.IsNullOrWhiteSpace(direct))
            {
                return direct;
            }
        }

        if (item.Links?.ExtensionData is not null
            && item.Links.ExtensionData.TryGetValue(propertyName, out var linkedValue)
            && linkedValue.ValueKind == JsonValueKind.Object
            && linkedValue.TryGetProperty("title", out var title)
            && title.ValueKind == JsonValueKind.String)
        {
            var linkedTitle = title.GetString();
            if (!string.IsNullOrWhiteSpace(linkedTitle))
            {
                return linkedTitle;
            }
        }

        return null;
    }

    private static string? FindCustomFieldPropertyName(Dictionary<string, OpenProjectSchemaCustomFieldDefinition> schemaFieldMap, string configuredFieldName)
    {
        return schemaFieldMap.FirstOrDefault(x => string.Equals(x.Value.Name, configuredFieldName, StringComparison.OrdinalIgnoreCase)).Key;
    }

    private static int ParseCustomFieldId(string propertyName)
    {
        const string Prefix = "customField";
        if (!propertyName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var suffix = propertyName[Prefix.Length..];
        return int.TryParse(suffix, out var id) ? id : 0;
    }

    private static string? ReadJsonValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.Null => null,
            _ => value.GetRawText()
        };
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string endpoint)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(_apiToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiToken);
        }

        return request;
    }

    private static string EscapeJson(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private async Task<Dictionary<string, object?>> BuildCreatePayloadAsync(
        WorkPackageTemplate sourceTemplate,
        GeneratedWorkPackage workPackage,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["subject"] = workPackage.Subject,
            ["startDate"] = workPackage.StartDate.ToString("yyyy-MM-dd"),
            ["dueDate"] = workPackage.DueDate.ToString("yyyy-MM-dd")
        };

        payload["description"] = new Dictionary<string, object?>
        {
            ["format"] = string.IsNullOrWhiteSpace(workPackage.DescriptionFormat) ? "markdown" : workPackage.DescriptionFormat,
            ["raw"] = workPackage.Description ?? string.Empty
        };

        var links = new Dictionary<string, object?>();
        links["type"] = new Dictionary<string, string> { ["href"] = $"/api/v3/types/{workPackage.TypeId}" };
        if (workPackage.ParentId > 0)
        {
            links["parent"] = new Dictionary<string, string> { ["href"] = $"/api/v3/work_packages/{workPackage.ParentId}" };
        }

        if (workPackage.AssigneeId is not null)
        {
            links["assignee"] = new Dictionary<string, string> { ["href"] = $"/api/v3/users/{workPackage.AssigneeId.Value}" };
        }

        if (workPackage.PriorityId is not null)
        {
            links["priority"] = new Dictionary<string, string> { ["href"] = $"/api/v3/priorities/{workPackage.PriorityId.Value}" };
        }

        if (workPackage.CategoryId is not null)
        {
            links["category"] = new Dictionary<string, string> { ["href"] = $"/api/v3/categories/{workPackage.CategoryId.Value}" };
        }

        var targetSchema = await GetSchemaDescriptorAsync(
            $"/api/v3/work_packages/schemas/{workPackage.ProjectId}-{workPackage.TypeId}",
            cancellationToken);

        var recurrenceFieldProperties = ResolveRecurrenceCustomFieldProperties(sourceTemplate.CustomFieldDefinitionsByProperty);
        foreach (var (propertyName, rawValue) in sourceTemplate.CustomFieldRawValuesByProperty)
        {
            if (recurrenceFieldProperties.Contains(propertyName)
                || !targetSchema.WritableProperties.Contains(propertyName))
            {
                continue;
            }

            var converted = ConvertJsonElement(rawValue);
            if (converted is null)
            {
                if (sourceTemplate.CustomFieldLinkHrefsByProperty.TryGetValue(propertyName, out var href)
                    && targetSchema.LinkProperties.Contains(propertyName))
                {
                    links[propertyName] = new Dictionary<string, string> { ["href"] = href };
                }

                continue;
            }

            payload[propertyName] = converted;
        }

        foreach (var (propertyName, href) in sourceTemplate.CustomFieldLinkHrefsByProperty)
        {
            if (recurrenceFieldProperties.Contains(propertyName)
                || links.ContainsKey(propertyName)
                || !targetSchema.WritableProperties.Contains(propertyName)
                || !targetSchema.LinkProperties.Contains(propertyName))
            {
                continue;
            }

            links[propertyName] = new Dictionary<string, string> { ["href"] = href };
        }

        payload["_links"] = links;
        return payload;
    }

    private IEnumerable<string> GetRequiredCustomFieldNames()
    {
        var fields = options.Value.CustomFields;

        yield return fields.RecurrenceEnabled;
        yield return fields.GeneratedType;
        yield return fields.RecurrenceType;
        yield return fields.Interval;
        yield return fields.NextOccurrence;
        yield return fields.GenerateAhead;
        yield return fields.DueAfter;
    }

    private WorkPackageTemplate? MapWorkPackage(
        OpenProjectWorkPackageItem item,
        int projectId,
        Dictionary<string, OpenProjectSchemaCustomFieldDefinition> schemaFieldMap,
        Dictionary<string, string?> customFields)
    {
        if (!customFields.TryGetValue(options.Value.CustomFields.RecurrenceEnabled, out var recurrenceEnabledValue)
            || !TryParseTrue(recurrenceEnabledValue))
        {
            return null;
        }

        customFields.TryGetValue(options.Value.CustomFields.GeneratedType, out var generatedTypeValue);
        customFields.TryGetValue(options.Value.CustomFields.RecurrenceType, out var recurrenceTypeValue);
        customFields.TryGetValue(options.Value.CustomFields.Interval, out var intervalValue);
        customFields.TryGetValue(options.Value.CustomFields.NextOccurrence, out var nextOccurrenceValue);
        customFields.TryGetValue(options.Value.CustomFields.GenerateAhead, out var generateAheadValue);
        customFields.TryGetValue(options.Value.CustomFields.DueAfter, out var dueAfterValue);

        var recurrenceType = ParseRecurrenceType(recurrenceTypeValue);

        int.TryParse(intervalValue, out var interval);
        int.TryParse(generateAheadValue, out var generateAhead);
        int.TryParse(dueAfterValue, out var dueAfter);

        var descriptionDetails = ParseDescriptionDetails(item.Description);

        var template = new WorkPackageTemplate
        {
            Id = item.Id,
            ProjectId = projectId,
            Subject = item.Subject ?? string.Empty,
            Description = descriptionDetails.Raw,
            DescriptionFormat = descriptionDetails.Format,
            AssigneeId = item.Assignee?.Id,
            PriorityId = item.Priority?.Id,
            CategoryId = item.Category?.Id,
            TypeId = item.Type?.Id,
            RecurrenceEnabled = true,
            GeneratedTypeName = generatedTypeValue ?? string.Empty,
            RecurrenceType = recurrenceType,
            Interval = interval <= 0 ? 1 : interval,
            GenerateAhead = generateAhead,
            DueAfter = dueAfter,
            NextOccurrence = ParseDateOnly(nextOccurrenceValue),
            Relations = item.Relations?.Select(x => new RelationLink { Type = x.Type ?? string.Empty, Id = x.Id, Name = x.Name }).ToList() ?? []
        };

        foreach (var (propertyName, definition) in schemaFieldMap)
        {
            template.CustomFieldDefinitionsByProperty[propertyName] = definition;

            if (item.ExtensionData is not null
                && item.ExtensionData.TryGetValue(propertyName, out var rawValue))
            {
                template.CustomFieldRawValuesByProperty[propertyName] = rawValue;
            }

            if (item.Links?.ExtensionData is not null
                && item.Links.ExtensionData.TryGetValue(propertyName, out var linkValue)
                && linkValue.ValueKind == JsonValueKind.Object
                && linkValue.TryGetProperty("href", out var hrefValue)
                && hrefValue.ValueKind == JsonValueKind.String)
            {
                var href = hrefValue.GetString();
                if (!string.IsNullOrWhiteSpace(href))
                {
                    template.CustomFieldLinkHrefsByProperty[propertyName] = href;
                }
            }
        }

        foreach (var customField in customFields)
        {
            template.CustomFields[customField.Key] = customField.Value;
        }

        return template;
    }

    private static bool TryParseTrue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "true" or "1" or "yes" or "ja";
    }

    private static RecurrenceType ParseRecurrenceType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RecurrenceType.Monthly;
        }

        var normalized = value.Trim();
        if (Enum.TryParse<RecurrenceType>(normalized.Replace(" ", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return normalized.ToLowerInvariant() switch
        {
            "daily" => RecurrenceType.Daily,
            "weekly" => RecurrenceType.Weekly,
            "monthly" => RecurrenceType.Monthly,
            "yearly" => RecurrenceType.Yearly,
            "after completion" => RecurrenceType.AfterCompletion,
            "aftercompletion" => RecurrenceType.AfterCompletion,
            _ => RecurrenceType.Monthly
        };
    }

    private static DateOnly? ParseDateOnly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateOnly.TryParse(value, out var date) ? date : null;
    }

    private static object? ConvertJsonElement(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when value.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Null => null,
            _ => JsonSerializer.Deserialize<object?>(value.GetRawText())
        };
    }

    private HashSet<string> ResolveRecurrenceCustomFieldProperties(Dictionary<string, OpenProjectSchemaCustomFieldDefinition> fieldDefinitionsByProperty)
    {
        var configuredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            options.Value.CustomFields.RecurrenceEnabled,
            options.Value.CustomFields.GeneratedType,
            options.Value.CustomFields.RecurrenceType,
            options.Value.CustomFields.Interval,
            options.Value.CustomFields.NextOccurrence,
            options.Value.CustomFields.GenerateAhead,
            options.Value.CustomFields.DueAfter
        };

        return fieldDefinitionsByProperty
            .Where(x => configuredNames.Contains(x.Value.Name))
            .Select(x => x.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<OpenProjectSchemaDescriptor> GetSchemaDescriptorAsync(string schemaHref, CancellationToken cancellationToken)
    {
        var endpoint = schemaHref.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? schemaHref
            : $"{_baseUrl}{schemaHref}";
        var request = CreateRequest(HttpMethod.Get, endpoint);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Unable to read schema '{schemaHref}'. Status: {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        var writable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var isWritable = property.Value.TryGetProperty("writable", out var writableProperty)
                && writableProperty.ValueKind == JsonValueKind.True;
            if (!isWritable)
            {
                continue;
            }

            writable.Add(property.Name);

            if (property.Value.TryGetProperty("_links", out var linksProperty)
                && linksProperty.ValueKind == JsonValueKind.Object)
            {
                links.Add(property.Name);
            }
        }

        return new OpenProjectSchemaDescriptor(writable, links);
    }

    private static OpenProjectDescriptionDetails ParseDescriptionDetails(JsonElement? value)
    {
        if (value is null)
        {
            return new OpenProjectDescriptionDetails("markdown", null);
        }

        var description = value.Value;
        if (description.ValueKind == JsonValueKind.String)
        {
            return new OpenProjectDescriptionDetails("markdown", description.GetString());
        }

        if (description.ValueKind == JsonValueKind.Object
            && description.TryGetProperty("raw", out var raw)
            && raw.ValueKind == JsonValueKind.String)
        {
            var format = description.TryGetProperty("format", out var formatProperty)
                         && formatProperty.ValueKind == JsonValueKind.String
                ? formatProperty.GetString() ?? "markdown"
                : "markdown";
            return new OpenProjectDescriptionDetails(format, raw.GetString());
        }

        return description.ValueKind == JsonValueKind.Null
            ? new OpenProjectDescriptionDetails("markdown", null)
            : new OpenProjectDescriptionDetails("markdown", description.GetRawText());
    }
}

public sealed class OpenProjectWorkPackageList
{
    [JsonPropertyName("_embedded")]
    public OpenProjectWorkPackageEmbedded? _Embedded { get; set; }
}

public sealed class OpenProjectWorkPackageEmbedded
{
    [JsonPropertyName("elements")]
    public List<OpenProjectWorkPackageItem> Elements { get; set; } = [];
}

public sealed class OpenProjectWorkPackageItem
{
    public int Id { get; set; }

    [JsonPropertyName("lockVersion")]
    public int LockVersion { get; set; }

    public string? Subject { get; set; }

    [JsonPropertyName("description")]
    public JsonElement? Description { get; set; }

    public OpenProjectProjectReference? Project { get; set; }

    public OpenProjectTypeReference? Type { get; set; }

    public OpenProjectReference? Assignee { get; set; }

    public OpenProjectReference? Priority { get; set; }

    public OpenProjectReference? Category { get; set; }

    public List<OpenProjectRelation>? Relations { get; set; }

    [JsonPropertyName("_links")]
    public OpenProjectWorkPackageLinks? Links { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class OpenProjectWorkPackageLinks
{
    [JsonPropertyName("schema")]
    public OpenProjectHrefLink? Schema { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class OpenProjectHrefLink
{
    [JsonPropertyName("href")]
    public string? Href { get; set; }
}

public sealed class OpenProjectProjectReference
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
}

public sealed class OpenProjectTypeReference
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class OpenProjectReference
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }
}

public sealed class OpenProjectRelation
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class OpenProjectProjectCollection
{
    [JsonPropertyName("_embedded")]
    public OpenProjectProjectEmbedded? _Embedded { get; set; }
}

public sealed class OpenProjectProjectEmbedded
{
    [JsonPropertyName("elements")]
    public List<OpenProjectProjectItem> Elements { get; set; } = [];
}

public sealed class OpenProjectProjectItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class OpenProjectTypeList
{
    [JsonPropertyName("_embedded")]
    public OpenProjectTypeEmbedded? _Embedded { get; set; }
}

public sealed class OpenProjectTypeEmbedded
{
    [JsonPropertyName("elements")]
    public List<OpenProjectTypeItem> Elements { get; set; } = [];
}

public sealed class OpenProjectTypeItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class OpenProjectCreatedResource
{
    [JsonPropertyName("id")]
    public int Id { get; set; }
}

public sealed record OpenProjectSchemaCustomFieldDefinition(int Id, string PropertyName, string Name);

public sealed record OpenProjectSchemaDescriptor(HashSet<string> WritableProperties, HashSet<string> LinkProperties);

public sealed record OpenProjectDescriptionDetails(string Format, string? Raw);
