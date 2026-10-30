using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vitrine.MercadoLivre.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialMercadoLivre : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "mercadolivre");

        migrationBuilder.CreateTable(
            name: "accounts",
            schema: "mercadolivre",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                ml_user_id = table.Column<long>(type: "bigint", nullable: false),
                nickname = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                site_id = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                access_token = table.Column<string>(type: "text", nullable: false),
                refresh_token = table.Column<string>(type: "text", nullable: false),
                access_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                connected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_accounts", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "pending_authorizations",
            schema: "mercadolivre",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                state_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                code_verifier = table.Column<string>(type: "text", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_pending_authorizations", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_accounts_ml_user_id",
            schema: "mercadolivre",
            table: "accounts",
            column: "ml_user_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_accounts_workspace_id",
            schema: "mercadolivre",
            table: "accounts",
            column: "workspace_id");

        migrationBuilder.CreateIndex(
            name: "IX_pending_authorizations_expires_at",
            schema: "mercadolivre",
            table: "pending_authorizations",
            column: "expires_at");

        migrationBuilder.CreateIndex(
            name: "IX_pending_authorizations_state_hash",
            schema: "mercadolivre",
            table: "pending_authorizations",
            column: "state_hash",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "accounts",
            schema: "mercadolivre");

        migrationBuilder.DropTable(
            name: "pending_authorizations",
            schema: "mercadolivre");
    }
}
