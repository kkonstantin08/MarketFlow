using Npgsql;

namespace MarketFlow.Tests;

public sealed class PostgresConnectionTests
{
    [Fact]
    public async Task Test_database_accepts_connections()
    {
        var connectionString = Environment.GetEnvironmentVariable("MARKETFLOW_TEST_DB")
            ?? "Host=localhost;Port=5433;Database=marketflow_test;Username=marketflow;Password=marketflow";
        await using var connection = new NpgsqlConnection(connectionString);

        await connection.OpenAsync();

        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }
}
