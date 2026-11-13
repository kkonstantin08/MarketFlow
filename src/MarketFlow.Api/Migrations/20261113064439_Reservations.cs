using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MarketFlow.Api.Migrations
{
    /// <inheritdoc />
    public partial class Reservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_supply",
                table: "StockMovements");

            migrationBuilder.AlterColumn<int>(
                name: "SupplyId",
                table: "StockMovements",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "ReservationId",
                table: "StockMovements",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Reservations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    WarehouseId = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reservations", x => x.Id);
                    table.CheckConstraint("ck_reservations_quantity_positive", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_Reservations_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Reservations_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_ReservationId",
                table: "StockMovements",
                column: "ReservationId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_operation",
                table: "StockMovements",
                sql: "(\"Type\" = 'supply' AND \"SupplyId\" IS NOT NULL AND \"ReservationId\" IS NULL AND \"OnHandDelta\" > 0 AND \"ReservedDelta\" = 0) OR (\"Type\" = 'reservation' AND \"SupplyId\" IS NULL AND \"ReservationId\" IS NOT NULL AND \"OnHandDelta\" = 0 AND \"ReservedDelta\" > 0)");

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_ProductId",
                table: "Reservations",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_Reservations_WarehouseId",
                table: "Reservations",
                column: "WarehouseId");

            migrationBuilder.AddForeignKey(
                name: "FK_StockMovements_Reservations_ReservationId",
                table: "StockMovements",
                column: "ReservationId",
                principalTable: "Reservations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StockMovements_Reservations_ReservationId",
                table: "StockMovements");

            migrationBuilder.DropTable(
                name: "Reservations");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_ReservationId",
                table: "StockMovements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_movements_operation",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "ReservationId",
                table: "StockMovements");

            migrationBuilder.AlterColumn<int>(
                name: "SupplyId",
                table: "StockMovements",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_movements_supply",
                table: "StockMovements",
                sql: "\"Type\" = 'supply' AND \"SupplyId\" > 0 AND \"OnHandDelta\" > 0 AND \"ReservedDelta\" = 0");
        }
    }
}
