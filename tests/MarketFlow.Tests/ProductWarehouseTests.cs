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
    private string _databaseConnectionString = null!;
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

    [Theory]
    [InlineData("/api/products")]
    [InlineData("/api/warehouses")]
    [InlineData("/api/products?q=%00")]
    public async Task Catalog_rejects_null_characters(string path)
    {
        using var response = path.Contains('?')
            ? await _client.GetAsync(path)
            : await _client.PostAsJsonAsync(path, new { name = "Invalid\0name", sku = "BAD-NAME" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("supplies")]
    [InlineData("reservations")]
    public async Task Inventory_rejects_quoted_quantities(string resource)
    {
        var (productId, warehouseId) = await CreateCatalogPair();
        using var response = await _client.PostAsJsonAsync($"/api/{resource}",
            new { reference = "NUMERIC-STRING", productId, warehouseId, quantity = "1" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty((await _client.GetFromJsonAsync<MovementResponse[]>("/api/stock-movements"))!);
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

    [Fact]
    public async Task Supply_replay_returns_the_original_result_and_changes_stock_once()
    {
        var (productId, warehouseId) = await CreateCatalogPair();
        var request = new { reference = " in-001 ", productId, warehouseId, quantity = 10 };

        var created = await _client.PostAsJsonAsync("/api/supplies", request);
        var replay = await _client.PostAsJsonAsync("/api/supplies", request);
        var supply = await created.Content.ReadFromJsonAsync<SupplyResponse>();
        var replayedSupply = await replay.Content.ReadFromJsonAsync<SupplyResponse>();
        var stocks = await _client.GetFromJsonAsync<StockResponse[]>("/api/stocks");
        var movements = await _client.GetFromJsonAsync<MovementResponse[]>("/api/stock-movements");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.NotNull(supply);
        Assert.Equal("IN-001", supply.Reference);
        Assert.Equal(supply, replayedSupply);
        Assert.NotNull(stocks);
        Assert.Equal(new StockResponse(productId, warehouseId, 10, 0, 10), Assert.Single(stocks));
        Assert.NotNull(movements);
        Assert.Equal(new MovementResponse(1, productId, warehouseId, "supply", supply.Id, 10, 0, supply.CreatedAt), Assert.Single(movements));
    }

    [Fact]
    public async Task Supply_conflicts_are_checked_before_references_and_new_unknown_references_return_404()
    {
        var (productId, warehouseId) = await CreateCatalogPair();
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-001", productId, warehouseId, quantity = 10 })).StatusCode);

        var conflict = await _client.PostAsJsonAsync("/api/supplies", new { reference = "in-001", productId = 999999, warehouseId = 999999, quantity = 9 });
        var unknownProduct = await _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-002", productId = 999999, warehouseId, quantity = 1 });
        var invalidReference = await _client.PostAsJsonAsync("/api/supplies", new { reference = "IN 003", productId = 999999, warehouseId, quantity = 1 });
        var invalidQuantity = await _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-004", productId, warehouseId, quantity = 0 });

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("supply_reference_conflict", await ReadCode(conflict));
        Assert.Equal(HttpStatusCode.NotFound, unknownProduct.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidReference.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidQuantity.StatusCode);
    }

    [Fact]
    public async Task Concurrent_supplies_with_different_references_add_without_lost_updates()
    {
        var (productId, warehouseId) = await CreateCatalogPair();

        var first = _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-001", productId, warehouseId, quantity = 10 });
        var second = _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-002", productId, warehouseId, quantity = 6 });
        var responses = await Task.WhenAll(first, second);
        var stocks = await _client.GetFromJsonAsync<StockResponse[]>("/api/stocks");
        var movements = await _client.GetFromJsonAsync<MovementResponse[]>("/api/stock-movements");

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        Assert.NotNull(stocks);
        Assert.Equal(new StockResponse(productId, warehouseId, 16, 0, 16), Assert.Single(stocks));
        Assert.NotNull(movements);
        Assert.Equal(2, movements.Length);
        Assert.Equal(16, movements.Sum(movement => movement.OnHandDelta));
    }

    [Fact]
    public async Task Concurrent_replays_of_one_reference_create_one_supply_and_one_movement()
    {
        var (productId, warehouseId) = await CreateCatalogPair();
        var request = new { reference = "IN-001", productId, warehouseId, quantity = 10 };

        var responses = await Task.WhenAll(
            _client.PostAsJsonAsync("/api/supplies", request),
            _client.PostAsJsonAsync("/api/supplies", request));
        var results = await Task.WhenAll(responses.Select(response => response.Content.ReadFromJsonAsync<SupplyResponse>()));
        var stocks = await _client.GetFromJsonAsync<StockResponse[]>("/api/stocks");
        var movements = await _client.GetFromJsonAsync<MovementResponse[]>("/api/stock-movements");

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Created], responses.Select(response => response.StatusCode).OrderBy(status => status));
        Assert.Equal(results[0]!.Id, results[1]!.Id);
        Assert.NotNull(stocks);
        Assert.Equal(10, Assert.Single(stocks).OnHand);
        Assert.NotNull(movements);
        Assert.Single(movements);
    }

    [Fact]
    public async Task Stock_limit_rejects_supply_without_persisting_its_fact_or_movement()
    {
        var (productId, warehouseId) = await CreateCatalogPair();
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-001", productId, warehouseId, quantity = int.MaxValue })).StatusCode);

        var overflow = await _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-002", productId, warehouseId, quantity = 1 });
        var stocks = await _client.GetFromJsonAsync<StockResponse[]>("/api/stocks");
        var movements = await _client.GetFromJsonAsync<MovementResponse[]>("/api/stock-movements");

        Assert.Equal(HttpStatusCode.Conflict, overflow.StatusCode);
        Assert.Equal("stock_limit", await ReadCode(overflow));
        Assert.Equal(1, await CountSupplies());
        Assert.NotNull(stocks);
        Assert.Equal(int.MaxValue, Assert.Single(stocks).OnHand);
        Assert.NotNull(movements);
        Assert.Single(movements);
    }

    [Fact]
    public async Task Concurrent_conflicting_replays_keep_only_the_winning_supply()
    {
        var (productId, warehouseId) = await CreateCatalogPair();
        var first = _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-001", productId, warehouseId, quantity = 10 });
        var second = _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-001", productId, warehouseId, quantity = 6 });
        var responses = await Task.WhenAll(first, second);
        var winner = Array.FindIndex(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.NotEqual(-1, winner);
        var loser = 1 - winner;
        var stocks = await _client.GetFromJsonAsync<StockResponse[]>("/api/stocks");
        var movements = await _client.GetFromJsonAsync<MovementResponse[]>("/api/stock-movements");

        Assert.Equal(HttpStatusCode.Conflict, responses[loser].StatusCode);
        Assert.Equal("supply_reference_conflict", await ReadCode(responses[loser]));
        Assert.NotNull(stocks);
        Assert.Equal(new StockResponse(productId, warehouseId, winner == 0 ? 10 : 6, 0, winner == 0 ? 10 : 6), Assert.Single(stocks));
        Assert.NotNull(movements);
        Assert.Single(movements);
    }

    [Fact]
    public async Task Failed_movement_insert_rolls_back_supply_and_stock_then_allows_retry()
    {
        var (productId, warehouseId) = await CreateCatalogPair();
        await ExecuteSql("""
            CREATE FUNCTION reject_supply_movement() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'test movement failure'; END $$;
            CREATE TRIGGER reject_supply_movement BEFORE INSERT ON "StockMovements"
            FOR EACH ROW EXECUTE FUNCTION reject_supply_movement();
            """);

        var failed = await _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-ROLLBACK", productId, warehouseId, quantity = 10 });
        var error = await failed.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.StartsWith("application/problem+json", failed.Content.Headers.ContentType?.ToString());
        Assert.DoesNotContain("test movement failure", error);
        Assert.Equal(0, await CountSupplies());
        Assert.Equal(0, await CountRows("Stocks"));
        Assert.Equal(0, await CountRows("StockMovements"));

        await ExecuteSql("DROP TRIGGER reject_supply_movement ON \"StockMovements\"; DROP FUNCTION reject_supply_movement();");
        var retry = await _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-ROLLBACK", productId, warehouseId, quantity = 10 });

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(1, await CountSupplies());
    }

    [Fact]
    public async Task Stock_and_movement_filters_and_pagination_apply_after_sorting()
    {
        var (productId, warehouseId) = await CreateCatalogPair();
        await CreateProduct("Nut", "NUT-01");
        var products = await _client.GetFromJsonAsync<ProductResponse[]>("/api/products");
        Assert.NotNull(products);
        var secondProductId = products.Single(product => product.Sku == "NUT-01").Id;
        await _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-001", productId, warehouseId, quantity = 5 });
        await _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-002", productId, warehouseId, quantity = 7 });
        await _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-003", productId = secondProductId, warehouseId, quantity = 3 });

        var stocks = await _client.GetFromJsonAsync<StockResponse[]>($"/api/stocks?productId={productId}&warehouseId={warehouseId}");
        var noStocks = await _client.GetFromJsonAsync<StockResponse[]>($"/api/stocks?productId=999999&warehouseId={warehouseId}");
        var movements = await _client.GetFromJsonAsync<MovementResponse[]>($"/api/stock-movements?productId={productId}&warehouseId={warehouseId}");
        var movementPage = await _client.GetFromJsonAsync<MovementResponse[]>($"/api/stock-movements?productId={productId}&limit=1&offset=1");

        Assert.NotNull(stocks);
        Assert.Equal(new StockResponse(productId, warehouseId, 12, 0, 12), Assert.Single(stocks));
        Assert.Empty(noStocks!);
        Assert.NotNull(movements);
        Assert.Equal(2, movements.Length);
        Assert.True(movements[0].Id > movements[1].Id);
        Assert.NotNull(movementPage);
        Assert.Equal(movements[1], Assert.Single(movementPage));
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/stocks?productId=0")).StatusCode);
    }

    [Fact]
    public async Task Reservations_reduce_available_stock_and_allow_reserving_the_exact_remainder()
    {
        var (productId, warehouseId) = await CreateCatalogPair();
        await _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-001", productId, warehouseId, quantity = 10 });

        var partial = await _client.PostAsJsonAsync("/api/reservations", new { productId, warehouseId, quantity = 4 });
        var exact = await _client.PostAsJsonAsync("/api/reservations", new { productId, warehouseId, quantity = 6 });
        var tooMuch = await _client.PostAsJsonAsync("/api/reservations", new { productId, warehouseId, quantity = 1 });
        var firstReservation = await partial.Content.ReadFromJsonAsync<ReservationResponse>();
        var stock = await _client.GetFromJsonAsync<StockResponse[]>($"/api/stocks?productId={productId}&warehouseId={warehouseId}");
        var movements = await _client.GetFromJsonAsync<MovementResponse[]>($"/api/stock-movements?productId={productId}&warehouseId={warehouseId}");

        Assert.Equal(HttpStatusCode.Created, partial.StatusCode);
        Assert.Equal(HttpStatusCode.Created, exact.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
        Assert.Equal("insufficient_stock", await ReadCode(tooMuch));
        Assert.NotNull(firstReservation);
        Assert.NotNull(stock);
        Assert.Equal(new StockResponse(productId, warehouseId, 10, 10, 0), Assert.Single(stock));
        Assert.NotNull(movements);
        Assert.Equal(3, movements.Length);
        Assert.Contains(movements, movement => movement.Type == "reservation" && movement.OperationId == firstReservation.Id && movement.OnHandDelta == 0 && movement.ReservedDelta == 4);
        Assert.Equal(2, await CountRows("Reservations"));
    }

    [Fact]
    public async Task Reservation_requires_existing_references_and_stock_and_valid_quantity()
    {
        var (productId, warehouseId) = await CreateCatalogPair();
        var missingStock = await _client.PostAsJsonAsync("/api/reservations", new { productId, warehouseId, quantity = 1 });
        var unknownProduct = await _client.PostAsJsonAsync("/api/reservations", new { productId = 999999, warehouseId, quantity = 1 });
        var unknownWarehouse = await _client.PostAsJsonAsync("/api/reservations", new { productId, warehouseId = 999999, quantity = 1 });
        var invalidQuantity = await _client.PostAsJsonAsync("/api/reservations", new { productId, warehouseId, quantity = -1 });
        var stocks = await _client.GetFromJsonAsync<StockResponse[]>("/api/stocks");
        var movements = await _client.GetFromJsonAsync<MovementResponse[]>("/api/stock-movements");

        Assert.Equal(HttpStatusCode.Conflict, missingStock.StatusCode);
        Assert.Equal("insufficient_stock", await ReadCode(missingStock));
        Assert.Equal(HttpStatusCode.NotFound, unknownProduct.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownWarehouse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidQuantity.StatusCode);
        Assert.Empty(stocks!);
        Assert.Empty(movements!);
        Assert.Equal(0, await CountRows("Reservations"));
        Assert.Equal(0, await CountRows("StockMovements"));
    }

    [Fact]
    public async Task Reservations_are_isolated_by_warehouse()
    {
        var (productId, firstWarehouseId) = await CreateCatalogPair();
        var secondWarehouseResponse = await _client.PostAsJsonAsync("/api/warehouses", new { name = "Second" });
        var secondWarehouse = await secondWarehouseResponse.Content.ReadFromJsonAsync<WarehouseResponse>();
        Assert.NotNull(secondWarehouse);
        await _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-001", productId, warehouseId = firstWarehouseId, quantity = 10 });
        await _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-002", productId, warehouseId = secondWarehouse.Id, quantity = 5 });

        var reserved = await _client.PostAsJsonAsync("/api/reservations", new { productId, warehouseId = firstWarehouseId, quantity = 4 });
        var firstStock = await _client.GetFromJsonAsync<StockResponse[]>($"/api/stocks?productId={productId}&warehouseId={firstWarehouseId}");
        var secondStock = await _client.GetFromJsonAsync<StockResponse[]>($"/api/stocks?productId={productId}&warehouseId={secondWarehouse.Id}");

        Assert.Equal(HttpStatusCode.Created, reserved.StatusCode);
        Assert.NotNull(firstStock);
        Assert.Equal(new StockResponse(productId, firstWarehouseId, 10, 4, 6), Assert.Single(firstStock));
        Assert.NotNull(secondStock);
        Assert.Equal(new StockResponse(productId, secondWarehouse.Id, 5, 0, 5), Assert.Single(secondStock));
    }

    [Fact]
    public async Task Concurrent_reservations_cannot_reserve_the_same_available_units_twice()
    {
        var (productId, warehouseId) = await CreateCatalogPair();
        await _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-001", productId, warehouseId, quantity = 10 });

        var responses = await Task.WhenAll(
            _client.PostAsJsonAsync("/api/reservations", new { productId, warehouseId, quantity = 7 }),
            _client.PostAsJsonAsync("/api/reservations", new { productId, warehouseId, quantity = 7 }));
        var rejected = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        var stocks = await _client.GetFromJsonAsync<StockResponse[]>($"/api/stocks?productId={productId}&warehouseId={warehouseId}");
        var movements = await _client.GetFromJsonAsync<MovementResponse[]>($"/api/stock-movements?productId={productId}&warehouseId={warehouseId}");

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Equal("insufficient_stock", await ReadCode(rejected));
        Assert.NotNull(stocks);
        Assert.Equal(new StockResponse(productId, warehouseId, 10, 7, 3), Assert.Single(stocks));
        Assert.NotNull(movements);
        Assert.Equal(2, movements.Length);
        Assert.Single(movements, movement => movement.Type == "reservation");
    }

    [Fact]
    public async Task Concurrent_supply_and_reservation_preserve_stock_and_history_invariants()
    {
        var (productId, warehouseId) = await CreateCatalogPair();
        await _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-001", productId, warehouseId, quantity = 5 });

        var supply = _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-002", productId, warehouseId, quantity = 3 });
        var reservation = _client.PostAsJsonAsync("/api/reservations", new { productId, warehouseId, quantity = 7 });
        var responses = await Task.WhenAll(supply, reservation);
        var stocks = await _client.GetFromJsonAsync<StockResponse[]>($"/api/stocks?productId={productId}&warehouseId={warehouseId}");
        var movements = await _client.GetFromJsonAsync<MovementResponse[]>($"/api/stock-movements?productId={productId}&warehouseId={warehouseId}");

        Assert.Equal(HttpStatusCode.Created, responses[0].StatusCode);
        Assert.Contains(responses[1].StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict });
        Assert.NotNull(stocks);
        var stock = Assert.Single(stocks);
        Assert.Equal(8, stock.OnHand);
        Assert.Equal(responses[1].StatusCode == HttpStatusCode.Created ? 7 : 0, stock.Reserved);
        Assert.InRange(stock.Available, 1, 8);
        Assert.NotNull(movements);
        Assert.Equal(responses[1].StatusCode == HttpStatusCode.Created ? 3 : 2, movements.Length);
    }

    [Fact]
    public async Task Failed_reservation_movement_insert_rolls_back_reserved_stock_and_reservation()
    {
        var (productId, warehouseId) = await CreateCatalogPair();
        await _client.PostAsJsonAsync("/api/supplies", new { reference = "IN-001", productId, warehouseId, quantity = 10 });
        await ExecuteSql("""
            CREATE FUNCTION reject_reservation_movement() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."Type" = 'reservation' THEN RAISE EXCEPTION 'test reservation failure'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER reject_reservation_movement BEFORE INSERT ON "StockMovements"
            FOR EACH ROW EXECUTE FUNCTION reject_reservation_movement();
            """);

        var failed = await _client.PostAsJsonAsync("/api/reservations", new { productId, warehouseId, quantity = 4 });

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(0, await CountRows("Reservations"));
        Assert.Equal(1, await CountRows("StockMovements"));
        var stock = await _client.GetFromJsonAsync<StockResponse[]>($"/api/stocks?productId={productId}&warehouseId={warehouseId}");
        Assert.NotNull(stock);
        Assert.Equal(new StockResponse(productId, warehouseId, 10, 0, 10), Assert.Single(stock));

        await ExecuteSql("DROP TRIGGER reject_reservation_movement ON \"StockMovements\"; DROP FUNCTION reject_reservation_movement();");
        var retry = await _client.PostAsJsonAsync("/api/reservations", new { productId, warehouseId, quantity = 4 });
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
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

        _databaseConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Database = _databaseName }.ConnectionString;
        _factory = new ApiFactory(_databaseConnectionString);
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

    private async Task<(int ProductId, int WarehouseId)> CreateCatalogPair()
    {
        await CreateProduct("Bolt", "BOLT-01");
        var product = await _client.GetFromJsonAsync<ProductResponse[]>("/api/products");
        var warehouseResponse = await _client.PostAsJsonAsync("/api/warehouses", new { name = "Central" });
        var warehouse = await warehouseResponse.Content.ReadFromJsonAsync<WarehouseResponse>();
        Assert.NotNull(product);
        Assert.NotNull(warehouse);
        return (Assert.Single(product).Id, warehouse.Id);
    }

    private async Task<int> CountSupplies()
    {
        await using var connection = new NpgsqlConnection(_databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM \"Supplies\"", connection);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<int> CountRows(string table)
    {
        await using var connection = new NpgsqlConnection(_databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT COUNT(*) FROM \"{table}\"", connection);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteSql(string sql)
    {
        await using var connection = new NpgsqlConnection(_databaseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ReadCode(HttpResponseMessage response)
    {
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private sealed record ProductResponse(int Id, string Name, string Sku);
    private sealed record WarehouseResponse(int Id, string Name);
    private sealed record SupplyResponse(int Id, string Reference, int ProductId, int WarehouseId, int Quantity, DateTime CreatedAt);
    private sealed record ReservationResponse(int Id, int ProductId, int WarehouseId, int Quantity, DateTime CreatedAt);
    private sealed record StockResponse(int ProductId, int WarehouseId, int OnHand, int Reserved, int Available);
    private sealed record MovementResponse(int Id, int ProductId, int WarehouseId, string Type, int OperationId, int OnHandDelta, int ReservedDelta, DateTime CreatedAt);

    private sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:MarketFlow", connectionString);
        }
    }
}
