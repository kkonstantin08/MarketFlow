using Microsoft.AspNetCore.Mvc.Testing;

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
        Assert.Contains("openapi", await document.Content.ReadAsStringAsync());
    }
}
