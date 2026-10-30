using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

// Código gerado pelo dotnet ef: arrays literais executados uma única vez por migração.
#pragma warning disable CA1861

namespace Vitrine.Listings.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialListings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "listings");

        migrationBuilder.AlterDatabase()
            .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

        migrationBuilder.CreateTable(
            name: "listings",
            schema: "listings",
            columns: table => new
            {
                account_id = table.Column<Guid>(type: "uuid", nullable: false),
                item_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, collation: "C"),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                price = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                currency_id = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                available_quantity = table.Column<int>(type: "integer", nullable: false),
                sold_quantity = table.Column<int>(type: "integer", nullable: false),
                status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                permalink = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                thumbnail_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                ml_last_updated = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                sync_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_listings", x => new { x.account_id, x.item_id });
            });

        migrationBuilder.CreateTable(
            name: "sync_states",
            schema: "listings",
            columns: table => new
            {
                account_id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                last_manual_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                item_count = table.Column<int>(type: "integer", nullable: false),
                last_error = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_sync_states", x => x.account_id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_listings_title_trgm",
            schema: "listings",
            table: "listings",
            column: "title")
            .Annotation("Npgsql:IndexMethod", "gin")
            .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

        migrationBuilder.CreateIndex(
            name: "ix_listings_workspace_last_updated",
            schema: "listings",
            table: "listings",
            columns: new[] { "workspace_id", "ml_last_updated", "item_id" },
            descending: new[] { false, true, false });

        migrationBuilder.CreateIndex(
            name: "ix_listings_workspace_status_last_updated",
            schema: "listings",
            table: "listings",
            columns: new[] { "workspace_id", "status", "ml_last_updated", "item_id" },
            descending: new[] { false, false, true, false });

        migrationBuilder.CreateIndex(
            name: "IX_sync_states_workspace_id",
            schema: "listings",
            table: "sync_states",
            column: "workspace_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "listings",
            schema: "listings");

        migrationBuilder.DropTable(
            name: "sync_states",
            schema: "listings");
    }
}
