namespace MarketFlow.Api;

public sealed class Supply
{
    public int Id { get; set; }
    public required string Reference { get; set; }
    public int ProductId { get; set; }
    public int WarehouseId { get; set; }
    public int Quantity { get; set; }
    public DateTime CreatedAt { get; set; }
}
