using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarketFlow.Api;

internal static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this WebApplication app)
    {
        app.MapPost("/api/products", async (CreateProductRequest? request, MarketFlowDbContext db, CancellationToken cancellationToken) =>
        {
            var name = request?.Name?.Trim();
            var sku = request?.Sku?.Trim();
            if (!ValidName(name) || !ValidSku(sku))
                return Error(StatusCodes.Status400BadRequest, "invalid_request", "Проверьте название и SKU товара.");

            var product = new Product { Name = name!, Sku = sku!.ToUpperInvariant() };
            db.Products.Add(product);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (IsDuplicateSku(exception))
            {
                return Error(StatusCodes.Status409Conflict, "duplicate_sku", "SKU уже используется.");
            }

            return Results.Created($"/api/products/{product.Id}", product);
        });

        app.MapGet("/api/products/{id:int}", async (int id, MarketFlowDbContext db, CancellationToken cancellationToken) =>
        {
            var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
            return product is null ? Results.NotFound() : Results.Ok(product);
        });

        app.MapGet("/api/products", async (MarketFlowDbContext db, string? q = null, int? limit = null, int? offset = null, CancellationToken cancellationToken = default) =>
        {
            if (!ValidPage(limit, offset))
                return Error(StatusCodes.Status400BadRequest, "invalid_pagination", "Проверьте limit и offset.");

            q = q?.Trim();
            if (q?.Length > 120)
                return Error(StatusCodes.Status400BadRequest, "invalid_query", "Запрос q не должен превышать 120 символов.");

            var products = db.Products.AsNoTracking();
            if (!string.IsNullOrEmpty(q))
            {
                var pattern = $"%{EscapeLike(q)}%";
                products = products.Where(product => EF.Functions.ILike(product.Name, pattern, "\\")
                    || EF.Functions.ILike(product.Sku, pattern, "\\"));
            }

            return Results.Ok(await products.OrderBy(product => product.Id)
                .Skip(offset ?? 0).Take(limit ?? 50).ToArrayAsync(cancellationToken));
        });

        app.MapPost("/api/warehouses", async (CreateWarehouseRequest? request, MarketFlowDbContext db, CancellationToken cancellationToken) =>
        {
            var name = request?.Name?.Trim();
            if (!ValidName(name))
                return Error(StatusCodes.Status400BadRequest, "invalid_request", "Проверьте название склада.");

            var warehouse = new Warehouse { Name = name! };
            db.Warehouses.Add(warehouse);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/warehouses/{warehouse.Id}", warehouse);
        });

        app.MapGet("/api/warehouses/{id:int}", async (int id, MarketFlowDbContext db, CancellationToken cancellationToken) =>
        {
            var warehouse = await db.Warehouses.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
            return warehouse is null ? Results.NotFound() : Results.Ok(warehouse);
        });

        app.MapGet("/api/warehouses", async (MarketFlowDbContext db, int? limit = null, int? offset = null, CancellationToken cancellationToken = default) =>
        {
            if (!ValidPage(limit, offset))
                return Error(StatusCodes.Status400BadRequest, "invalid_pagination", "Проверьте limit и offset.");

            return Results.Ok(await db.Warehouses.AsNoTracking().OrderBy(warehouse => warehouse.Id)
                .Skip(offset ?? 0).Take(limit ?? 50).ToArrayAsync(cancellationToken));
        });
    }

    internal static IResult Error(int statusCode, string code, string title) =>
        Results.Problem(statusCode: statusCode, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });

    private static bool ValidName(string? name) => name is { Length: > 0 and <= 120 };

    internal static bool ValidSku(string? sku) => sku is { Length: > 0 and <= 40 }
        && sku.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');

    internal static bool ValidPage(int? limit, int? offset) => limit is null or >= 1 and <= 100 && offset is null or >= 0;

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static bool IsDuplicateSku(DbUpdateException exception) => exception.InnerException is PostgresException
    {
        SqlState: PostgresErrorCodes.UniqueViolation,
        ConstraintName: "uq_products_sku"
    };
}

public sealed record CreateProductRequest(string? Name, string? Sku);
public sealed record CreateWarehouseRequest(string? Name);
