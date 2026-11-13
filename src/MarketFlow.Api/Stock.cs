namespace MarketFlow.Api;

public sealed class Stock
{
    public int ProductId { get; set; }
    public int WarehouseId { get; set; }
    public int OnHand { get; set; }
    public int Reserved { get; set; }
}
