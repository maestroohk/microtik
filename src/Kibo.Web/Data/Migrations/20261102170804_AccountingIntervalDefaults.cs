using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kibo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountingIntervalDefaults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE packages SET accounting_interval_seconds = 60 WHERE accounting_interval_seconds = 0;
                UPDATE subscriptions SET accounting_interval_seconds = 60 WHERE accounting_interval_seconds = 0;
                UPDATE radius.access SET accounting_interval_seconds = 60 WHERE accounting_interval_seconds = 0;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
