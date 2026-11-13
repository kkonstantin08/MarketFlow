using Microsoft.EntityFrameworkCore;
using MarketFlow.Api;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("MarketFlow")
    ?? throw new InvalidOperationException("Connection string 'MarketFlow' is required.");
builder.Services.AddDbContext<MarketFlowDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddProblemDetails();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseStatusCodePages();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.MapGet("/health", () => Results.Text("ok"));
app.MapCatalogEndpoints();

await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<MarketFlowDbContext>().Database.MigrateAsync();

app.Run();

public partial class Program { }
