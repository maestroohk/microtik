using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kibo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class RouterProvisioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "claim_token",
                table: "routers",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "drift_detected_at",
                table: "routers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "hardware_id",
                table: "routers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ownership_verified_at",
                table: "routers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "posture_checked_at",
                table: "routers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "posture_json",
                table: "routers",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "provisioning_operations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    router_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    plan_json = table.Column<string>(type: "text", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: false),
                    requested_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provisioning_operations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "router_backups",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    router_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    export_protected = table.Column<string>(type: "text", nullable: false),
                    export_length = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_router_backups", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_routers_hardware_id",
                table: "routers",
                column: "hardware_id",
                unique: true,
                filter: "hardware_id IS NOT NULL");

            migrationBuilder.Sql("UPDATE routers SET ownership_verified_at = created_at WHERE id IN (SELECT router_id FROM radius.nas);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provisioning_operations");

            migrationBuilder.DropTable(
                name: "router_backups");

            migrationBuilder.DropIndex(
                name: "ix_routers_hardware_id",
                table: "routers");

            migrationBuilder.DropColumn(
                name: "claim_token",
                table: "routers");

            migrationBuilder.DropColumn(
                name: "drift_detected_at",
                table: "routers");

            migrationBuilder.DropColumn(
                name: "hardware_id",
                table: "routers");

            migrationBuilder.DropColumn(
                name: "ownership_verified_at",
                table: "routers");

            migrationBuilder.DropColumn(
                name: "posture_checked_at",
                table: "routers");

            migrationBuilder.DropColumn(
                name: "posture_json",
                table: "routers");
        }
    }
}
