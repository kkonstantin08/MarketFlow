namespace MarketFlow.Api;

public sealed class StockMovement
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public int WarehouseId { get; set; }
    public int? SupplyId { get; set; }
    public int? ReservationId { get; set; }
    public required string Type { get; set; }
    public int OnHandDelta { get; set; }
    public int ReservedDelta { get; set; }
    public DateTime CreatedAt { get; set; }
}
