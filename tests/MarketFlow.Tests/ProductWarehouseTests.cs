using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using MarketFlow.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MarketFlow.Tests;

public sealed class ProductWarehouseTests : IAsyncLifetime
{
    private readonly string _databaseName = $"marketflow_{Guid.NewGuid():N}";
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [Fact]
    public async Task Products_can_be_created_read_and_searched_case_insensitively()
    {
        var response = await _client.PostAsJsonAsync("/api/products", new { name = "Кабель USB", sku = " box-01 " });

        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var product = await response.Content.ReadFromJsonAsync<ProductResponse>();
        Assert.NotNull(product);
        Assert.Equal("Кабель USB", product.Name);
        Assert.Equal("BOX-01", product.Sku);

        var byId = await _client.GetFromJsonAsync<ProductResponse>($"/api/products/{product.Id}");
        var byName = await _client.GetFromJsonAsync<ProductResponse[]>("/api/products?q=%D0%BA%D0%B0%D0%B1");
        var bySku = await _client.GetFromJsonAsync<ProductResponse[]>("/api/products?q=box-01");

        Assert.Equal(product, byId);
        Assert.NotNull(byName);
        Assert.NotNull(bySku);
        Assert.Equal([product], byName);
        Assert.Equal([product], bySku);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/products/999999")).StatusCode);
    }

    [Fact]
    public async Task Product_validation_and_duplicate_sku_return_contract_errors()
    {
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/products", new { name = "Box", sku = " box-01 " })).StatusCode);

        var duplicate = await _client.PostAsJsonAsync("/api/products", new { name = "Another", sku = "BOX-01" });
        var invalid = await _client.PostAsJsonAsync("/api/products", new { name = " ", sku = "bad sku" });
        var nonAsciiSku = await _client.PostAsJsonAsync("/api/products", new { name = "Unicode", sku = "ı" });
        var longName = await _client.PostAsJsonAsync("/api/products", new { name = new string('x', 121), sku = "LONG-NAME" });
        var longSku = await _client.PostAsJsonAsync("/api/products", new { name = "Long SKU", sku = new string('x', 41) });
        var nullBody = await _client.PostAsync("/api/products", new StringContent("null", Encoding.UTF8, "application/json"));
        var products = await _client.GetFromJsonAsync<ProductResponse[]>("/api/products");

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("duplicate_sku", await ReadCode(duplicate));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("invalid_request", await ReadCode(invalid));
        Assert.Equal(HttpStatusCode.BadRequest, nonAsciiSku.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longSku.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, nullBody.StatusCode);
        Assert.Single(products!);
    }

    [Fact]
    public async Task Product_search_trims_query_and_rejects_values_over_120_characters()
    {
        await CreateProduct("Steel bolt", "BOLT-01");

        var trimmed = await _client.GetFromJsonAsync<ProductResponse[]>("/api/products?q=%20%20BOLT%20%20");
        var empty = await _client.GetFromJsonAsync<ProductResponse[]>("/api/products?q=%20%20");
        var tooLong = await _client.GetAsync($"/api/products?q={new string('x', 121)}");

        Assert.NotNull(trimmed);
        Assert.NotNull(empty);
        Assert.Single(trimmed);
        Assert.Single(empty);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Product_search_treats_percent_and_underscore_literally_and_paginates_by_id()
    {
        await CreateProduct("Part%One", "PERCENT");
        await CreateProduct("PartXOne", "WILDCARD");
        await CreateProduct("Cable_01", "UNDERSCORE");
        await CreateProduct("CableA01", "OTHER");

        var percent = await _client.GetFromJsonAsync<ProductResponse[]>("/api/products?q=%25");
        var underscore = await _client.GetFromJsonAsync<ProductResponse[]>("/api/products?q=_");
        var page = await _client.GetFromJsonAsync<ProductResponse[]>("/api/products?limit=2&offset=1");
        var invalidType = await _client.GetAsync("/api/products?limit=abc");

        Assert.NotNull(percent);
        Assert.NotNull(underscore);
        Assert.NotNull(page);
        Assert.Equal(["PERCENT"], percent!.Select(product => product.Sku));
        Assert.Equal(["UNDERSCORE"], underscore!.Select(product => product.Sku));
        Assert.Equal(["WILDCARD", "UNDERSCORE"], page!.Select(product => product.Sku));
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/products?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidType.StatusCode);
        Assert.StartsWith("application/problem+json", invalidType.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task Warehouses_can_be_created_listed_and_read_by_id()
    {
        var created = await _client.PostAsJsonAsync("/api/warehouses", new { name = "  Central  " });
        var duplicateName = await _client.PostAsJsonAsync("/api/warehouses", new { name = "Central" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var warehouse = await created.Content.ReadFromJsonAsync<WarehouseResponse>();
        Assert.NotNull(warehouse);
        Assert.Equal("Central", warehouse.Name);
        Assert.Equal(HttpStatusCode.Created, duplicateName.StatusCode);
        Assert.Equal(warehouse, await _client.GetFromJsonAsync<WarehouseResponse>($"/api/warehouses/{warehouse.Id}"));
        var warehouses = await _client.GetFromJsonAsync<WarehouseResponse[]>("/api/warehouses");
        Assert.NotNull(warehouses);
        Assert.Equal([warehouse.Id, warehouse.Id + 1], warehouses.Select(item => item.Id));
        var secondPage = await _client.GetFromJsonAsync<WarehouseResponse[]>("/api/warehouses?limit=1&offset=1");
        Assert.NotNull(secondPage);
        Assert.Equal(warehouse.Id + 1, Assert.Single(secondPage).Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/warehouses", new { name = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/warehouses", new { name = new string('x', 121) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/warehouses/999999")).StatusCode);
    }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("MARKETFLOW_TEST_DB")
            ?? "Host=localhost;Port=5433;Database=marketflow_test;Username=marketflow;Password=marketflow";
        var adminConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString;
        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var databaseConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Database = _databaseName }.ConnectionString;
        _factory = new ApiFactory(databaseConnectionString);
        _client = _factory.CreateClient();
        using var scope = _factory.Services.CreateScope();
        Assert.Equal(_databaseName, scope.ServiceProvider.GetRequiredService<MarketFlowDbContext>().Database.GetDbConnection().Database);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        var connectionString = Environment.GetEnvironmentVariable("MARKETFLOW_TEST_DB")
            ?? "Host=localhost;Port=5433;Database=marketflow_test;Username=marketflow;Password=marketflow";
        var adminConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString;
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{_databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task CreateProduct(string name, string sku)
    {
        var response = await _client.PostAsJsonAsync("/api/products", new { name, sku });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string?> ReadCode(HttpResponseMessage response)
    {
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private sealed record ProductResponse(int Id, string Name, string Sku);
    private sealed record WarehouseResponse(int Id, string Name);

    private sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:MarketFlow", connectionString);
        }
    }
}
