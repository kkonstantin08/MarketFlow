namespace MarketFlow.Api;

public sealed class Reservation
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public int WarehouseId { get; set; }
    public int Quantity { get; set; }
    public DateTime CreatedAt { get; set; }
}
