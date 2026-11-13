namespace MarketFlow.Api;

public sealed class Product
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Sku { get; set; }
}
