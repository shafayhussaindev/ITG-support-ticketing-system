using System.Net;
using System.Text.Json;

namespace SupportTicketing.IntegrationTests;

/// <summary>
/// The published API description being generatable at all.
/// </summary>
/// <remarks>
/// Auth and Admin each declare a <c>TeamMembershipResponse</c>, deliberately different
/// shapes — only the admin view carries <c>CapacityWeight</c>. Swashbuckle keys schemas
/// by short name, and that collision did not degrade the document, it failed the whole
/// generation: <c>/swagger/v1/swagger.json</c> returned 500 and Swagger UI showed
/// "Failed to load API definition" with not one endpoint listed.
///
/// Nothing noticed, because every API route kept serving requests normally. The only
/// casualty was the page a newcomer opens first to find out what the API can do.
/// </remarks>
[Collection(nameof(ApiCollection))]
public class ApiDocumentationTests(ApiFactory factory)
{
    private async Task<JsonDocument> GetDocumentAsync()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/swagger/v1/swagger.json");

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_openapi_document_generates()
    {
        using var document = await GetDocumentAsync();

        var paths = document.RootElement.GetProperty("paths");

        // A document that generated but described nothing would satisfy a bare 200,
        // and is the same failure from the reader's point of view.
        paths.EnumerateObject().Count().ShouldBeGreaterThan(50);
        paths.TryGetProperty("/api/v1/notifications", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Contracts_sharing_a_short_name_each_keep_their_own_schema()
    {
        using var document = await GetDocumentAsync();

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        schemas.TryGetProperty("AuthTeamMembershipResponse", out var self).ShouldBeTrue();
        schemas.TryGetProperty("AdminTeamMembershipResponse", out var admin).ShouldBeTrue();

        // The distinction that makes them two types rather than one: a user reading
        // their own profile has no business seeing their routing weight.
        admin.GetProperty("properties").TryGetProperty("capacityWeight", out _).ShouldBeTrue();
        self.GetProperty("properties").TryGetProperty("capacityWeight", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Every_schema_reference_resolves()
    {
        using var document = await GetDocumentAsync();

        var declared = document.RootElement
            .GetProperty("components").GetProperty("schemas")
            .EnumerateObject().Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        // Disambiguating schema ids renames the components; a rename that missed a
        // reference would leave the document parseable but broken in the viewer,
        // which is exactly the class of failure this file exists to catch.
        var referenced = References(document.RootElement).ToList();

        referenced.ShouldNotBeEmpty();
        referenced.Where(name => !declared.Contains(name)).ShouldBeEmpty();
    }

    private static IEnumerable<string> References(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("$ref") && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var reference = property.Value.GetString()!;
                        if (reference.StartsWith("#/components/schemas/", StringComparison.Ordinal))
                        {
                            yield return reference["#/components/schemas/".Length..];
                        }
                    }

                    foreach (var nested in References(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in References(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }
}
