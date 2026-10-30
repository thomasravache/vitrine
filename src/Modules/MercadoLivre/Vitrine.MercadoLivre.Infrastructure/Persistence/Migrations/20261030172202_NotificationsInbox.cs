using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vitrine.MercadoLivre.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class NotificationsInbox : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "notifications",
            schema: "mercadolivre",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                notification_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                topic = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                resource = table.Column<string>(type: "text", nullable: false),
                ml_user_id = table.Column<long>(type: "bigint", nullable: false),
                application_id = table.Column<long>(type: "bigint", nullable: false),
                attempts = table.Column<int>(type: "integer", nullable: false),
                sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_notifications", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_notifications_notification_id",
            schema: "mercadolivre",
            table: "notifications",
            column: "notification_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_notifications_processed_at",
            schema: "mercadolivre",
            table: "notifications",
            column: "processed_at");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "notifications",
            schema: "mercadolivre");
    }
}
