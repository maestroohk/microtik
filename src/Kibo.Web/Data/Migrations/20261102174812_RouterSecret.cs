using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kibo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class RouterSecret : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "router_secret",
                table: "access_accounts",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE access_accounts SET router_secret = replace(gen_random_uuid()::text, '-', '') WHERE router_secret = '';
                UPDATE radius.access ra SET password = aa.router_secret
                FROM access_accounts aa WHERE aa.id = ra.access_account_id AND aa.kind <> 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "router_secret",
                table: "access_accounts");
        }
    }
}
