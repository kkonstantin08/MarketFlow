using Microsoft.AspNetCore.Mvc.Testing;
using System.Text.Json;

namespace MarketFlow.Tests;

public sealed class ApiSmokeTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_endpoint_returns_ok()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Swagger_ui_and_openapi_document_are_available()
    {
        using var client = factory.CreateClient();

        var ui = await client.GetAsync("/swagger/index.html");
        var document = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(System.Net.HttpStatusCode.OK, ui.StatusCode);
        Assert.Contains("swagger-ui", await ui.Content.ReadAsStringAsync());
        Assert.Equal(System.Net.HttpStatusCode.OK, document.StatusCode);
        using var specification = JsonDocument.Parse(await document.Content.ReadAsStringAsync());
        var paths = specification.RootElement.GetProperty("paths");
        foreach (var (resource, schema) in new[]
        {
            ("products", "Product"), ("warehouses", "Warehouse"),
            ("supplies", "Supply"), ("reservations", "Reservation")
        })
        {
            var responses = paths.GetProperty($"/api/{resource}").GetProperty("post").GetProperty("responses");
            AssertSchema("201", schema);
            Assert.True(responses.TryGetProperty("400", out _));
            if (resource == "supplies")
                AssertSchema("200", schema);
            else
                Assert.False(responses.TryGetProperty("200", out _));

            void AssertSchema(string status, string type) =>
                Assert.Equal($"#/components/schemas/{type}", responses.GetProperty(status).GetProperty("content")
                    .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        }
    }

    [Fact]
    public async Task Demo_page_and_assets_are_available()
    {
        using var client = factory.CreateClient();

        var page = await client.GetAsync("/");
        var styles = await client.GetAsync("/styles.css");
        var script = await client.GetAsync("/app.js");
        var pageContent = await page.Content.ReadAsStringAsync();
        var styleContent = await styles.Content.ReadAsStringAsync();
        var scriptContent = await script.Content.ReadAsStringAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Приёмка", pageContent);
        Assert.Contains("<label for=\"product-id\">Товар</label>", pageContent);
        Assert.Contains("<label for=\"warehouse-id\">Склад</label>", pageContent);
        Assert.Contains("id=\"product-form\"", pageContent);
        Assert.Contains("id=\"warehouse-form\"", pageContent);
        Assert.Equal("text/css", styles.Content.Headers.ContentType?.MediaType);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Contains(":focus-visible", styleContent);
        Assert.Contains("@media (max-width: 680px)", styleContent);
        Assert.Contains("/api/supplies", scriptContent);
        Assert.Contains("/api/reservations", scriptContent);
        Assert.Contains("/api/products", scriptContent);
        Assert.Contains("/api/warehouses", scriptContent);
    }
}
