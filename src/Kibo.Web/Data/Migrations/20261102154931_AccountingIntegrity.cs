using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kibo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountingIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_radacct_acctuniqueid",
                schema: "radius",
                table: "radacct");

            migrationBuilder.AddColumn<DateTime>(
                name: "nas_event_time",
                schema: "radius",
                table: "radacct",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "router_id",
                schema: "radius",
                table: "radacct",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "subscription_id",
                schema: "radius",
                table: "radacct",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "radius",
                table: "radacct",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "update_count",
                schema: "radius",
                table: "radacct",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "acct_events",
                schema: "radius",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    nas_ip_address = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status_type = table.Column<string>(type: "text", nullable: false),
                    session_id = table.Column<string>(type: "text", nullable: true),
                    user_name = table.Column<string>(type: "text", nullable: true),
                    session_time = table.Column<long>(type: "bigint", nullable: false),
                    input_bytes = table.Column<long>(type: "bigint", nullable: false),
                    output_bytes = table.Column<long>(type: "bigint", nullable: false),
                    nas_event_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    @class = table.Column<string>(name: "class", type: "text", nullable: true),
                    rad_acct_id = table.Column<long>(type: "bigint", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_acct_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_radacct_acctuniqueid",
                schema: "radius",
                table: "radacct",
                column: "acctuniqueid");

            migrationBuilder.CreateIndex(
                name: "ix_radacct_nasipaddress_acctsessionid_username",
                schema: "radius",
                table: "radacct",
                columns: new[] { "nasipaddress", "acctsessionid", "username" });

            migrationBuilder.CreateIndex(
                name: "ix_radacct_subscription_id_acctstoptime",
                schema: "radius",
                table: "radacct",
                columns: new[] { "subscription_id", "acctstoptime" });

            migrationBuilder.CreateIndex(
                name: "ix_acct_events_nas_ip_address_session_id",
                schema: "radius",
                table: "acct_events",
                columns: new[] { "nas_ip_address", "session_id" });

            migrationBuilder.CreateIndex(
                name: "ix_acct_events_received_at",
                schema: "radius",
                table: "acct_events",
                column: "received_at");

            migrationBuilder.Sql("""
                UPDATE radius.radacct r SET tenant_id = n.tenant_id, router_id = n.router_id
                FROM radius.nas n WHERE n.nas_name = r.nasipaddress AND r.tenant_id IS NULL;

                UPDATE radius.radacct r SET subscription_id = s.id
                FROM public.subscriptions s
                JOIN public.access_accounts a ON a.id = s.access_account_id
                WHERE r.subscription_id IS NULL AND r.class ~ '^[0-9a-fA-F-]{36}$' AND s.id = r.class::uuid
                  AND a.username = r.username AND a.tenant_id = r.tenant_id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "acct_events",
                schema: "radius");

            migrationBuilder.DropIndex(
                name: "ix_radacct_acctuniqueid",
                schema: "radius",
                table: "radacct");

            migrationBuilder.DropIndex(
                name: "ix_radacct_nasipaddress_acctsessionid_username",
                schema: "radius",
                table: "radacct");

            migrationBuilder.DropIndex(
                name: "ix_radacct_subscription_id_acctstoptime",
                schema: "radius",
                table: "radacct");

            migrationBuilder.DropColumn(
                name: "nas_event_time",
                schema: "radius",
                table: "radacct");

            migrationBuilder.DropColumn(
                name: "router_id",
                schema: "radius",
                table: "radacct");

            migrationBuilder.DropColumn(
                name: "subscription_id",
                schema: "radius",
                table: "radacct");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "radius",
                table: "radacct");

            migrationBuilder.DropColumn(
                name: "update_count",
                schema: "radius",
                table: "radacct");

            migrationBuilder.CreateIndex(
                name: "ix_radacct_acctuniqueid",
                schema: "radius",
                table: "radacct",
                column: "acctuniqueid",
                unique: true);
        }
    }
}
