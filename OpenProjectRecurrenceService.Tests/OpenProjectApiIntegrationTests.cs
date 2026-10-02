using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace OpenProjectRecurrenceService.Tests;

[TestClass]
public sealed class OpenProjectApiIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] RequiredRecurrenceFieldNames =
    [
        "Recurrence enabled",
        "Recurrence type",
        "Interval",
        "Generate ahead",
        "Due after",
        "Generated type",
        "Next occurrence"
    ];

    [TestMethod]
    public async Task ReadWorkPackages_FromConfiguredProject()
    {
        var settings = LoadSettings();
        var project = await GetProjectByNameAsync(settings, settings.ProjectName);
        var result = await GetAsync<OpenProjectWorkPackageCollection>($"{settings.BaseUrl}/api/v3/projects/{project.Id}/work_packages?include[]=customFields&limit=10");

        Assert.IsNotNull(result);
        Assert.IsNotNull(result!._Embedded);
        Assert.IsTrue(result._Embedded.Elements.Count > 0, "No work packages were returned for the configured project.");
    }

    [TestMethod]
    public async Task ReadWorkPackageTypes_FromTheApi()
    {
        var settings = LoadSettings();
        var result = await GetAsync<OpenProjectTypeCollection>($"{settings.BaseUrl}/api/v3/types?limit=10");

        Assert.IsNotNull(result);
        Assert.IsNotNull(result!._Embedded);
        Assert.IsTrue(result._Embedded.Elements.Count > 0, "The OpenProject API did not return any work package types.");
    }

    [TestMethod]
    public async Task Spec_ShowsHowCustomFieldDefinitionsAreExposed()
    {
        var settings = LoadSettings();
        using var spec = await GetJsonAsync($"{settings.BaseUrl}/api/v3/spec.json", settings.ApiKey);

        if (!spec.RootElement.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
        {
            Assert.Fail("OpenProject spec.json did not contain a valid 'paths' section.");
        }

        var hasCustomFieldsCollection = paths.TryGetProperty("/api/v3/custom_fields", out _);
        var hasCustomFieldsById = paths.TryGetProperty("/api/v3/custom_fields/{id}", out _);

        Assert.IsFalse(hasCustomFieldsCollection, "This OpenProject instance unexpectedly advertises /api/v3/custom_fields.");
        Assert.IsTrue(hasCustomFieldsById, "Expected /api/v3/custom_fields/{id} to exist in the API spec.");
    }

    [TestMethod]
    public async Task ReadCustomFields_FromWorkPackagePayload()
    {
        var settings = LoadSettings();
        var project = await GetProjectByNameAsync(settings, settings.ProjectName);
        using var workPackages = await GetJsonAsync($"{settings.BaseUrl}/api/v3/projects/{project.Id}/work_packages?limit=1", settings.ApiKey);
        var elements = ReadEmbeddedElements(workPackages.RootElement);
        Assert.IsTrue(elements.Count > 0, $"Expected at least one work package in project '{project.Name}'.");

        var schemaHref = ReadSchemaHref(elements[0]);
        using var schema = await GetJsonAsync($"{settings.BaseUrl}{schemaHref}", settings.ApiKey);
        var mapByProperty = ReadSchemaCustomFieldMap(schema.RootElement);
        var names = mapByProperty.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Assert.IsTrue(names.Count > 0, "No custom fields were returned from OpenProject.");

        var missing = RequiredRecurrenceFieldNames
            .Where(required => names.All(name => !string.Equals(name, required, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (missing.Count > 0)
        {
            Assert.Fail($"OpenProject is missing required recurrence custom field definitions: {string.Join(", ", missing)}");
        }

        TestContext.WriteLine($"Discovered custom fields: {string.Join(", ", names)}");
    }

    [TestMethod]
    public async Task FindProjectWithRecurrenceConfiguration()
    {
        var settings = LoadSettings();
        var project = await GetProjectByNameAsync(settings, settings.ProjectName);
        using var payload = await GetJsonAsync($"{settings.BaseUrl}/api/v3/projects/{project.Id}/work_packages?include[]=customFields&limit=100", settings.ApiKey);
        var elements = ReadEmbeddedElements(payload.RootElement);
        var schemaCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in elements)
        {
            var schemaHref = ReadSchemaHref(element);
            if (!schemaCache.TryGetValue(schemaHref, out var schemaByProperty))
            {
                using var schema = await GetJsonAsync($"{settings.BaseUrl}{schemaHref}", settings.ApiKey);
                schemaByProperty = ReadSchemaCustomFieldMap(schema.RootElement);
                schemaCache[schemaHref] = schemaByProperty;
            }

            var valuesByName = ReadCustomFieldValuesFromWorkPackage(element, schemaByProperty);
            var recurrenceEnabled = TryParseTruthy(valuesByName.GetValueOrDefault("Recurrence enabled"));
            if (!recurrenceEnabled)
            {
                continue;
            }

            var wpId = element.TryGetProperty("id", out var idProperty) && idProperty.TryGetInt32(out var numericId) ? numericId : 0;
            var subject = element.TryGetProperty("subject", out var subjectProperty) ? subjectProperty.GetString() : null;
            var recurrenceType = valuesByName.GetValueOrDefault("Recurrence type");
            var nextOccurrence = valuesByName.GetValueOrDefault("Next occurrence");

            TestContext.WriteLine($"Found recurrence-configured work package #{wpId} '{subject ?? "<unknown>"}' in project '{project.Name}' (type='{recurrenceType}', next='{nextOccurrence}').");
            return;
        }

        Assert.Fail($"No recurrence-configured work package was found in project '{project.Name}'.");
    }

    [TestMethod]
    public async Task CreateAndDelete_TestWorkPackage()
    {
        var settings = LoadSettings();
        var createdId = 0;
        var testSubject = $"MSTest API cleanup work package {DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";

        var payload = new
        {
            subject = testSubject,
            project = new { id = settings.ProjectId },
            type = new { id = 1 }
        };

        try
        {
            var created = await PostAsync<OpenProjectWorkPackage>($"{settings.BaseUrl}/api/v3/workspaces/{settings.ProjectId}/work_packages", payload, settings.ApiKey);
            Assert.IsNotNull(created);
            Assert.AreNotEqual(0, created!.Id);
            createdId = created.Id;

            TestContext.WriteLine($"Created temporary work package #{created.Id} ('{testSubject}').");
        }
        finally
        {
            if (createdId > 0)
            {
                using var client = CreateHttpClient(settings);
                using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"{settings.BaseUrl}/api/v3/work_packages/{createdId}");
                using var deleteResponse = await client.SendAsync(deleteRequest);
                var status = deleteResponse.StatusCode;

                if (status != HttpStatusCode.NoContent && status != HttpStatusCode.OK && status != HttpStatusCode.NotFound)
                {
                    var responseBody = await deleteResponse.Content.ReadAsStringAsync();
                    Assert.Fail($"Cleanup failed for temporary work package #{createdId}. Status: {(int)status} {status}. Response: {responseBody}");
                }

                TestContext.WriteLine($"Cleanup delete status for temporary work package #{createdId}: {(int)status} {status}.");
            }
        }
    }

    private static OpenProjectTestSettings LoadSettings()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<OpenProjectApiIntegrationTests>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var baseUrl = configuration["OpenProject:baseUrl"] ?? configuration["OpenProject:BaseUrl"];
        var apiKey = configuration["OpenProject:apiKey"] ?? configuration["OpenProject:ApiKey"];
        var projectName = configuration["OpenProject:ProjectName"] ?? "RevoCloud ISMS";

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            Assert.Inconclusive("OpenProject user secrets are not configured. Add OpenProject:baseUrl and OpenProject:apiKey to the local user secrets store.");
        }

        var project = FindProjectByNameAsync(baseUrl, apiKey, projectName).GetAwaiter().GetResult();

        return new OpenProjectTestSettings(baseUrl, apiKey, projectName, project.Id);
    }

    private static async Task<OpenProjectProject> FindProjectByNameAsync(string baseUrl, string apiKey, string projectName)
    {
        var projectCollection = await GetAsync<OpenProjectProjectCollection>($"{baseUrl}/api/v3/projects?limit=50", apiKey);
        var project = projectCollection?._Embedded?.Elements.FirstOrDefault(x => string.Equals(x.Name, projectName, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(project, $"Project '{projectName}' was not found in OpenProject.");
        return project!;
    }

    private static async Task<OpenProjectProject> GetProjectByNameAsync(OpenProjectTestSettings settings, string projectName)
    {
        return await FindProjectByNameAsync(settings.BaseUrl, settings.ApiKey, projectName);
    }

    private static HttpClient CreateHttpClient(OpenProjectTestSettings settings)
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static async Task<T?> GetAsync<T>(string endpoint, string? apiKey = null)
    {
        apiKey ??= LoadSettings().ApiKey;
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.GetAsync(endpoint);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(content, JsonOptions);
    }

    private static async Task<JsonDocument> GetJsonAsync(string endpoint, string apiKey)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.GetAsync(endpoint);
        var content = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(content);
    }

    private static async Task<T?> PostAsync<T>(string endpoint, object payload, string? apiKey = null)
    {
        apiKey ??= LoadSettings().ApiKey;
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.PostAsJsonAsync(endpoint, payload);
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"POST {endpoint} failed with {(int)response.StatusCode} {response.StatusCode}. Response: {content}");
        }

        return JsonSerializer.Deserialize<T>(content, JsonOptions);
    }

    private static List<JsonElement> ReadEmbeddedElements(JsonElement root)
    {
        if (!root.TryGetProperty("_embedded", out var embedded))
        {
            throw new AssertFailedException("Expected '_embedded' in OpenProject response.");
        }

        if (!embedded.TryGetProperty("elements", out var elements)
            || elements.ValueKind != JsonValueKind.Array)
        {
            throw new AssertFailedException("Expected '_embedded.elements' in OpenProject response.");
        }

        var result = new List<JsonElement>();
        foreach (var element in elements.EnumerateArray())
        {
            result.Add(element);
        }

        return result;
    }

    private static string ReadSchemaHref(JsonElement workPackage)
    {
        if (!workPackage.TryGetProperty("_links", out var links)
            || !links.TryGetProperty("schema", out var schema)
            || !schema.TryGetProperty("href", out var href)
            || href.ValueKind != JsonValueKind.String)
        {
            throw new AssertFailedException("Work package did not provide '_links.schema.href'.");
        }

        return href.GetString()!;
    }

    private static Dictionary<string, string> ReadSchemaCustomFieldMap(JsonElement schema)
    {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in schema.EnumerateObject())
        {
            if (!property.Name.StartsWith("customField", StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!property.Value.TryGetProperty("name", out var nameProperty) || nameProperty.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = nameProperty.GetString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                output[property.Name] = name;
            }
        }

        return output;
    }

    private static Dictionary<string, string?> ReadCustomFieldValuesFromWorkPackage(JsonElement workPackage, Dictionary<string, string> schemaByProperty)
    {
        var output = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in workPackage.EnumerateObject())
        {
            if (!property.Name.StartsWith("customField", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!schemaByProperty.TryGetValue(property.Name, out var fieldName))
            {
                continue;
            }

            output[fieldName] = ReadJsonValue(property.Value);
        }

        return output;
    }

    private static bool TryParseTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "true" or "1" or "yes" or "y" or "ja";
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

    private sealed record OpenProjectTestSettings(string BaseUrl, string ApiKey, string ProjectName, int ProjectId);

    private sealed class OpenProjectProjectCollection
    {
        [JsonPropertyName("_embedded")]
        public OpenProjectProjectEmbedded? _Embedded { get; set; }
    }

    private sealed class OpenProjectProjectEmbedded
    {
        [JsonPropertyName("elements")]
        public List<OpenProjectProject> Elements { get; set; } = [];
    }

    private sealed class OpenProjectProject
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class OpenProjectWorkPackageCollection
    {
        [JsonPropertyName("_embedded")]
        public OpenProjectWorkPackageEmbedded? _Embedded { get; set; }
    }

    private sealed class OpenProjectWorkPackageEmbedded
    {
        [JsonPropertyName("elements")]
        public List<OpenProjectWorkPackage> Elements { get; set; } = [];
    }

    private sealed class OpenProjectWorkPackage
    {
        public int Id { get; set; }

        public string Subject { get; set; } = string.Empty;

        [JsonPropertyName("customFields")]
        public List<OpenProjectCustomField>? CustomFields { get; set; }
    }

    private sealed class OpenProjectCustomField
    {
        public string Name { get; set; } = string.Empty;

        public string? Value { get; set; }
    }

    private sealed class OpenProjectTypeCollection
    {
        [JsonPropertyName("_embedded")]
        public OpenProjectTypeEmbedded? _Embedded { get; set; }
    }

    private sealed class OpenProjectTypeEmbedded
    {
        [JsonPropertyName("elements")]
        public List<OpenProjectType> Elements { get; set; } = [];
    }

    private sealed class OpenProjectType
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
