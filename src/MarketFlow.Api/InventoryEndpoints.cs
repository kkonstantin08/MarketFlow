using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace MarketFlow.Api;

internal static class InventoryEndpoints
{
    public static void MapInventoryEndpoints(this WebApplication app)
    {
        app.MapPost("/api/supplies", async (CreateSupplyRequest? request, MarketFlowDbContext db, CancellationToken cancellationToken) =>
        {
            var reference = request?.Reference?.Trim();
            if (!CatalogEndpoints.ValidSku(reference) || request!.ProductId <= 0 || request.WarehouseId <= 0 || request.Quantity <= 0)
                return CatalogEndpoints.Error(StatusCodes.Status400BadRequest, "invalid_request", "Проверьте номер поставки, ссылки и количество.");

            reference = reference!.ToUpperInvariant();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var existing = await db.Supplies.AsNoTracking().FirstOrDefaultAsync(supply => supply.Reference == reference, cancellationToken);
            if (existing is not null)
                return ExistingSupply(existing, request.ProductId, request.WarehouseId, request.Quantity);

            if (!await db.Products.AnyAsync(product => product.Id == request.ProductId, cancellationToken)
                || !await db.Warehouses.AnyAsync(warehouse => warehouse.Id == request.WarehouseId, cancellationToken))
                return Results.NotFound();

            var supplyId = await ExecuteScalarAsync(db, """
                INSERT INTO "Supplies" ("Reference", "ProductId", "WarehouseId", "Quantity")
                VALUES (@p0, @p1, @p2, @p3)
                ON CONFLICT ("Reference") DO NOTHING
                RETURNING "Id" AS "Value"
                """, cancellationToken, reference, request.ProductId, request.WarehouseId, request.Quantity);

            if (supplyId == 0)
            {
                existing = await db.Supplies.AsNoTracking().SingleAsync(supply => supply.Reference == reference, cancellationToken);
                return ExistingSupply(existing, request.ProductId, request.WarehouseId, request.Quantity);
            }

            var stockChanged = await ExecuteScalarAsync(db, """
                INSERT INTO "Stocks" ("ProductId", "WarehouseId", "OnHand", "Reserved")
                VALUES (@p0, @p1, @p2, 0)
                ON CONFLICT ("ProductId", "WarehouseId") DO UPDATE
                SET "OnHand" = "Stocks"."OnHand" + EXCLUDED."OnHand"
                WHERE "Stocks"."OnHand" <= 2147483647 - EXCLUDED."OnHand"
                RETURNING 1 AS "Value"
                """, cancellationToken, request.ProductId, request.WarehouseId, request.Quantity);

            if (stockChanged == 0)
                return CatalogEndpoints.Error(StatusCodes.Status409Conflict, "stock_limit", "Превышен максимальный остаток.");

            db.StockMovements.Add(new StockMovement
            {
                ProductId = request.ProductId,
                WarehouseId = request.WarehouseId,
                SupplyId = supplyId,
                Type = "supply",
                OnHandDelta = request.Quantity,
                ReservedDelta = 0
            });
            await db.SaveChangesAsync(cancellationToken);
            var supply = await db.Supplies.AsNoTracking().SingleAsync(item => item.Id == supplyId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.Created($"/api/supplies/{supply.Id}", supply);
        });

        app.MapPost("/api/reservations", async (CreateReservationRequest? request, MarketFlowDbContext db, CancellationToken cancellationToken) =>
        {
            if (request is null || request.ProductId <= 0 || request.WarehouseId <= 0 || request.Quantity <= 0)
                return CatalogEndpoints.Error(StatusCodes.Status400BadRequest, "invalid_request", "Проверьте ссылки и количество резерва.");

            if (!await db.Products.AnyAsync(product => product.Id == request.ProductId, cancellationToken)
                || !await db.Warehouses.AnyAsync(warehouse => warehouse.Id == request.WarehouseId, cancellationToken))
                return Results.NotFound();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var stockChanged = await ExecuteScalarAsync(db, """
                UPDATE "Stocks"
                SET "Reserved" = "Reserved" + @p0
                WHERE "ProductId" = @p1 AND "WarehouseId" = @p2 AND "OnHand" - "Reserved" >= @p0
                RETURNING 1 AS "Value"
                """, cancellationToken, request.Quantity, request.ProductId, request.WarehouseId);

            if (stockChanged == 0)
                return CatalogEndpoints.Error(StatusCodes.Status409Conflict, "insufficient_stock", "Недостаточно доступного остатка.");

            var reservation = new Reservation
            {
                ProductId = request.ProductId,
                WarehouseId = request.WarehouseId,
                Quantity = request.Quantity
            };
            db.Reservations.Add(reservation);
            await db.SaveChangesAsync(cancellationToken);

            db.StockMovements.Add(new StockMovement
            {
                ProductId = request.ProductId,
                WarehouseId = request.WarehouseId,
                ReservationId = reservation.Id,
                Type = "reservation",
                OnHandDelta = 0,
                ReservedDelta = request.Quantity
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.Created($"/api/reservations/{reservation.Id}", reservation);
        });

        app.MapGet("/api/stocks", async (MarketFlowDbContext db, int? productId = null, int? warehouseId = null, int? limit = null, int? offset = null, CancellationToken cancellationToken = default) =>
        {
            if (!CatalogEndpoints.ValidPage(limit, offset) || productId is <= 0 || warehouseId is <= 0)
                return CatalogEndpoints.Error(StatusCodes.Status400BadRequest, "invalid_filter", "Проверьте фильтры, limit и offset.");

            var stocks = db.Stocks.AsNoTracking();
            if (productId is not null)
                stocks = stocks.Where(stock => stock.ProductId == productId);
            if (warehouseId is not null)
                stocks = stocks.Where(stock => stock.WarehouseId == warehouseId);

            return Results.Ok(await stocks.OrderBy(stock => stock.WarehouseId).ThenBy(stock => stock.ProductId)
                .Skip(offset ?? 0).Take(limit ?? 50)
                .Select(stock => new StockResponse(stock.ProductId, stock.WarehouseId, stock.OnHand, stock.Reserved, stock.OnHand - stock.Reserved))
                .ToArrayAsync(cancellationToken));
        });

        app.MapGet("/api/stock-movements", async (MarketFlowDbContext db, int? productId = null, int? warehouseId = null, int? limit = null, int? offset = null, CancellationToken cancellationToken = default) =>
        {
            if (!CatalogEndpoints.ValidPage(limit, offset) || productId is <= 0 || warehouseId is <= 0)
                return CatalogEndpoints.Error(StatusCodes.Status400BadRequest, "invalid_filter", "Проверьте фильтры, limit и offset.");

            var movements = db.StockMovements.AsNoTracking();
            if (productId is not null)
                movements = movements.Where(movement => movement.ProductId == productId);
            if (warehouseId is not null)
                movements = movements.Where(movement => movement.WarehouseId == warehouseId);

            return Results.Ok(await movements.OrderByDescending(movement => movement.CreatedAt).ThenByDescending(movement => movement.Id)
                .Skip(offset ?? 0).Take(limit ?? 50)
                .Select(movement => new StockMovementResponse(movement.Id, movement.ProductId, movement.WarehouseId,
                    movement.Type, movement.SupplyId ?? movement.ReservationId!.Value, movement.OnHandDelta, movement.ReservedDelta, movement.CreatedAt))
                .ToArrayAsync(cancellationToken));
        });
    }

    private static IResult ExistingSupply(Supply supply, int productId, int warehouseId, int quantity) =>
        supply.ProductId == productId && supply.WarehouseId == warehouseId && supply.Quantity == quantity
            ? Results.Ok(supply)
            : CatalogEndpoints.Error(StatusCodes.Status409Conflict, "supply_reference_conflict", "Номер поставки уже использован с другими данными.");

    private static async Task<int> ExecuteScalarAsync(MarketFlowDbContext db, string sql, CancellationToken cancellationToken, params object[] values)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var index = 0;
        foreach (var value in values)
            command.Parameters.AddWithValue($"p{index++}", value);
        return (int?)await command.ExecuteScalarAsync(cancellationToken) ?? 0;
    }
}

public sealed record CreateSupplyRequest(string? Reference, int ProductId, int WarehouseId, int Quantity);
public sealed record CreateReservationRequest(int ProductId, int WarehouseId, int Quantity);
public sealed record StockResponse(int ProductId, int WarehouseId, int OnHand, int Reserved, int Available);
public sealed record StockMovementResponse(int Id, int ProductId, int WarehouseId, string Type, int OperationId, int OnHandDelta, int ReservedDelta, DateTime CreatedAt);
