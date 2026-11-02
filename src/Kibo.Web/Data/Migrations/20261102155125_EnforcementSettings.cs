using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kibo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnforcementSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "accounting_interval_seconds",
                table: "subscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "fair_use_enforcement",
                table: "subscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "throttle_overshoot_bytes",
                table: "subscriptions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "usage_observed_at",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "accounting_interval_seconds",
                table: "packages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "fair_use_enforcement",
                table: "packages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "accounting_interval_seconds",
                schema: "radius",
                table: "access",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "fair_use_router_cutoff",
                schema: "radius",
                table: "access",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "accounting_interval_seconds",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "fair_use_enforcement",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "throttle_overshoot_bytes",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "usage_observed_at",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "accounting_interval_seconds",
                table: "packages");

            migrationBuilder.DropColumn(
                name: "fair_use_enforcement",
                table: "packages");

            migrationBuilder.DropColumn(
                name: "accounting_interval_seconds",
                schema: "radius",
                table: "access");

            migrationBuilder.DropColumn(
                name: "fair_use_router_cutoff",
                schema: "radius",
                table: "access");
        }
    }
}
