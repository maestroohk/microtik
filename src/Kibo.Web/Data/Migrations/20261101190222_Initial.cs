using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kibo.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "radius");

            migrationBuilder.CreateTable(
                name: "access",
                schema: "radius",
                columns: table => new
                {
                    username = table.Column<string>(type: "text", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    password = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    service_type = table.Column<string>(type: "text", nullable: false),
                    counting_since = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    validity_seconds = table.Column<long>(type: "bigint", nullable: true),
                    activate_by = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    data_limit_bytes = table.Column<long>(type: "bigint", nullable: true),
                    time_limit_seconds = table.Column<long>(type: "bigint", nullable: true),
                    max_session_seconds = table.Column<int>(type: "integer", nullable: true),
                    rate_limit = table.Column<string>(type: "text", nullable: false),
                    throttled_rate_limit = table.Column<string>(type: "text", nullable: true),
                    simultaneous_use = table.Column<int>(type: "integer", nullable: false),
                    window_start = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    window_end = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    time_zone = table.Column<string>(type: "text", nullable: false),
                    framed_ip_address = table.Column<string>(type: "text", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access", x => new { x.tenant_id, x.username });
                });

            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    phone_number = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    commission_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    credit_limit = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_name = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: true),
                    details = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "background_jobs",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    deduplication_key = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    run_after = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    locked_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_background_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ledger_entries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    voucher_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    description = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ledger_entries", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "nas",
                schema: "radius",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    nas_name = table.Column<string>(type: "text", nullable: false),
                    short_name = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    secret = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    router_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_nas", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "packages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    service_type = table.Column<int>(type: "integer", nullable: false),
                    price = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    validity_amount = table.Column<int>(type: "integer", nullable: false),
                    validity_unit = table.Column<int>(type: "integer", nullable: false),
                    validity_start = table.Column<int>(type: "integer", nullable: false),
                    activation_deadline_days = table.Column<int>(type: "integer", nullable: false),
                    time_quota_minutes = table.Column<int>(type: "integer", nullable: true),
                    data_quota_megabytes = table.Column<long>(type: "bigint", nullable: true),
                    max_session_minutes = table.Column<int>(type: "integer", nullable: true),
                    download_kbps = table.Column<int>(type: "integer", nullable: false),
                    upload_kbps = table.Column<int>(type: "integer", nullable: false),
                    throttled_download_kbps = table.Column<int>(type: "integer", nullable: true),
                    throttled_upload_kbps = table.Column<int>(type: "integer", nullable: true),
                    max_devices = table.Column<int>(type: "integer", nullable: false),
                    access_window_start = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    access_window_end = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    grace_minutes = table.Column<int>(type: "integer", nullable: false),
                    is_recurring = table.Column<bool>(type: "boolean", nullable: false),
                    is_promotional = table.Column<bool>(type: "boolean", nullable: false),
                    available_from = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    available_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    show_on_portal = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_packages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payment_callbacks",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    provider = table.Column<string>(type: "text", nullable: false),
                    provider_reference = table.Column<string>(type: "text", nullable: true),
                    payment_reference = table.Column<string>(type: "text", nullable: true),
                    signature_valid = table.Column<bool>(type: "boolean", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payment_callbacks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "text", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    access_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    method = table.Column<int>(type: "integer", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false),
                    provider_reference = table.Column<string>(type: "text", nullable: true),
                    phone_number = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    client_mac_address = table.Column<string>(type: "text", nullable: true),
                    client_ip_address = table.Column<string>(type: "text", nullable: true),
                    portal_token = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    verification_attempts = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "radacct",
                schema: "radius",
                columns: table => new
                {
                    radacctid = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    acctsessionid = table.Column<string>(type: "text", nullable: false),
                    acctuniqueid = table.Column<string>(type: "text", nullable: false),
                    username = table.Column<string>(type: "text", nullable: true),
                    nasipaddress = table.Column<string>(type: "text", nullable: true),
                    nasportid = table.Column<string>(type: "text", nullable: true),
                    acctstarttime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    acctupdatetime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    acctstoptime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    acctsessiontime = table.Column<long>(type: "bigint", nullable: true),
                    acctinputoctets = table.Column<long>(type: "bigint", nullable: true),
                    acctoutputoctets = table.Column<long>(type: "bigint", nullable: true),
                    calledstationid = table.Column<string>(type: "text", nullable: true),
                    callingstationid = table.Column<string>(type: "text", nullable: true),
                    acctterminatecause = table.Column<string>(type: "text", nullable: true),
                    framedipaddress = table.Column<string>(type: "text", nullable: true),
                    @class = table.Column<string>(name: "class", type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_radacct", x => x.radacctid);
                });

            migrationBuilder.CreateTable(
                name: "radpostauth",
                schema: "radius",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    username = table.Column<string>(type: "text", nullable: false),
                    reply = table.Column<string>(type: "text", nullable: true),
                    callingstationid = table.Column<string>(type: "text", nullable: true),
                    nasipaddress = table.Column<string>(type: "text", nullable: true),
                    authdate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_radpostauth", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "router_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    router_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_router_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "router_health_samples",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    router_id = table.Column<Guid>(type: "uuid", nullable: false),
                    taken_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    cpu_load_percent = table.Column<int>(type: "integer", nullable: false),
                    free_memory_bytes = table.Column<long>(type: "bigint", nullable: false),
                    total_memory_bytes = table.Column<long>(type: "bigint", nullable: false),
                    uptime_seconds = table.Column<long>(type: "bigint", nullable: false),
                    active_hotspot_users = table.Column<int>(type: "integer", nullable: false),
                    rx_bits_per_second = table.Column<long>(type: "bigint", nullable: false),
                    tx_bits_per_second = table.Column<long>(type: "bigint", nullable: false),
                    is_synthetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_router_health_samples", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    location = table.Column<string>(type: "text", nullable: false),
                    portal_title = table.Column<string>(type: "text", nullable: false),
                    portal_tagline = table.Column<string>(type: "text", nullable: false),
                    portal_accent = table.Column<string>(type: "text", nullable: false),
                    support_phone = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sites", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "support_notes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    author = table.Column<string>(type: "text", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_support_notes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    slug = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    suspension_reason = table.Column<string>(type: "text", nullable: true),
                    plan = table.Column<int>(type: "integer", nullable: false),
                    max_routers = table.Column<int>(type: "integer", nullable: false),
                    pppoe_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    agents_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    country_code = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    time_zone = table.Column<string>(type: "text", nullable: false),
                    contact_email = table.Column<string>(type: "text", nullable: false),
                    contact_phone = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "voucher_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    redeem_by = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_voucher_batches", x => x.id);
                    table.ForeignKey(
                        name: "fk_voucher_batches_packages_package_id",
                        column: x => x.package_id,
                        principalTable: "packages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    full_name = table.Column<string>(type: "text", nullable: false),
                    phone_number = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: true),
                    home_site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customers", x => x.id);
                    table.ForeignKey(
                        name: "fk_customers_sites_home_site_id",
                        column: x => x.home_site_id,
                        principalTable: "sites",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "routers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    api_kind = table.Column<int>(type: "integer", nullable: false),
                    management_host = table.Column<string>(type: "text", nullable: false),
                    management_port = table.Column<int>(type: "integer", nullable: false),
                    use_tls = table.Column<bool>(type: "boolean", nullable: false),
                    allow_self_signed_certificate = table.Column<bool>(type: "boolean", nullable: false),
                    api_username = table.Column<string>(type: "text", nullable: false),
                    api_password_protected = table.Column<string>(type: "text", nullable: false),
                    nas_ip_address = table.Column<string>(type: "text", nullable: false),
                    radius_secret = table.Column<string>(type: "text", nullable: false),
                    coa_port = table.Column<int>(type: "integer", nullable: false),
                    hotspot_interface = table.Column<string>(type: "text", nullable: false),
                    uplink_interface = table.Column<string>(type: "text", nullable: false),
                    provisioning_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    identity = table.Column<string>(type: "text", nullable: true),
                    board_name = table.Column<string>(type: "text", nullable: true),
                    router_os_version = table.Column<string>(type: "text", nullable: true),
                    architecture = table.Column<string>(type: "text", nullable: true),
                    serial_number = table.Column<string>(type: "text", nullable: true),
                    supports_rest_api = table.Column<bool>(type: "boolean", nullable: true),
                    hotspot_present = table.Column<bool>(type: "boolean", nullable: true),
                    radius_configured = table.Column<bool>(type: "boolean", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_checked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    next_check_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_routers", x => x.id);
                    table.ForeignKey(
                        name: "fk_routers_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    full_name = table.Column<string>(type: "text", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<int>(type: "integer", nullable: false),
                    is_platform_admin = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    failed_login_count = table.Column<int>(type: "integer", nullable: false),
                    locked_until = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_login_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.ForeignKey(
                        name: "fk_users_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "vouchers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    access_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    sold_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    redeemed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancel_reason = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_vouchers", x => x.id);
                    table.ForeignKey(
                        name: "fk_vouchers_voucher_batches_batch_id",
                        column: x => x.batch_id,
                        principalTable: "voucher_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "access_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    username = table.Column<string>(type: "text", nullable: false),
                    password = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    static_ip_address = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_access_accounts", x => x.id);
                    table.ForeignKey(
                        name: "fk_access_accounts_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "customer_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mac_address = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    first_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_customer_devices_customers_customer_id",
                        column: x => x.customer_id,
                        principalTable: "customers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    access_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_name = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false),
                    source_reference = table.Column<string>(type: "text", nullable: true),
                    price_paid = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    validity_amount = table.Column<int>(type: "integer", nullable: false),
                    validity_unit = table.Column<int>(type: "integer", nullable: false),
                    validity_start = table.Column<int>(type: "integer", nullable: false),
                    data_quota_bytes = table.Column<long>(type: "bigint", nullable: true),
                    time_quota_seconds = table.Column<long>(type: "bigint", nullable: true),
                    max_session_seconds = table.Column<int>(type: "integer", nullable: true),
                    download_kbps = table.Column<int>(type: "integer", nullable: false),
                    upload_kbps = table.Column<int>(type: "integer", nullable: false),
                    throttled_download_kbps = table.Column<int>(type: "integer", nullable: true),
                    throttled_upload_kbps = table.Column<int>(type: "integer", nullable: true),
                    max_devices = table.Column<int>(type: "integer", nullable: false),
                    access_window_start = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    access_window_end = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    grace_seconds = table.Column<int>(type: "integer", nullable: false),
                    is_recurring = table.Column<bool>(type: "boolean", nullable: false),
                    purchased_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    activate_by = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    counting_since = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    activated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    used_bytes = table.Column<long>(type: "bigint", nullable: false),
                    used_seconds = table.Column<long>(type: "bigint", nullable: false),
                    usage_updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    throttled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    end_reason = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_subscriptions_access_accounts_access_account_id",
                        column: x => x.access_account_id,
                        principalTable: "access_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_subscriptions_packages_package_id",
                        column: x => x.package_id,
                        principalTable: "packages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_access_access_account_id",
                schema: "radius",
                table: "access",
                column: "access_account_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_access_accounts_customer_id",
                table: "access_accounts",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_access_accounts_tenant_id_username",
                table: "access_accounts",
                columns: new[] { "tenant_id", "username" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_tenant_id_created_at",
                table: "audit_logs",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_background_jobs_deduplication_key",
                table: "background_jobs",
                column: "deduplication_key",
                unique: true,
                filter: "deduplication_key IS NOT NULL AND status IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "ix_background_jobs_status_run_after",
                table: "background_jobs",
                columns: new[] { "status", "run_after" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_devices_customer_id",
                table: "customer_devices",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "ix_customer_devices_tenant_id_mac_address",
                table: "customer_devices",
                columns: new[] { "tenant_id", "mac_address" });

            migrationBuilder.CreateIndex(
                name: "ix_customers_home_site_id",
                table: "customers",
                column: "home_site_id");

            migrationBuilder.CreateIndex(
                name: "ix_customers_tenant_id_phone_number",
                table: "customers",
                columns: new[] { "tenant_id", "phone_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_payment_id_kind",
                table: "ledger_entries",
                columns: new[] { "payment_id", "kind" },
                unique: true,
                filter: "payment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_ledger_entries_tenant_id_created_at",
                table: "ledger_entries",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_nas_nas_name",
                schema: "radius",
                table: "nas",
                column: "nas_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_portal_token",
                table: "payments",
                column: "portal_token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_provider_provider_reference",
                table: "payments",
                columns: new[] { "provider", "provider_reference" },
                unique: true,
                filter: "provider_reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_payments_reference",
                table: "payments",
                column: "reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_status_created_at",
                table: "payments",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_radacct_acctuniqueid",
                schema: "radius",
                table: "radacct",
                column: "acctuniqueid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_radacct_nasipaddress_acctstoptime",
                schema: "radius",
                table: "radacct",
                columns: new[] { "nasipaddress", "acctstoptime" });

            migrationBuilder.CreateIndex(
                name: "ix_radacct_username_acctstoptime",
                schema: "radius",
                table: "radacct",
                columns: new[] { "username", "acctstoptime" });

            migrationBuilder.CreateIndex(
                name: "ix_radpostauth_authdate",
                schema: "radius",
                table: "radpostauth",
                column: "authdate");

            migrationBuilder.CreateIndex(
                name: "ix_router_events_router_id_occurred_at",
                table: "router_events",
                columns: new[] { "router_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_router_health_samples_router_id_taken_at",
                table: "router_health_samples",
                columns: new[] { "router_id", "taken_at" });

            migrationBuilder.CreateIndex(
                name: "ix_routers_nas_ip_address",
                table: "routers",
                column: "nas_ip_address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_routers_site_id",
                table: "routers",
                column: "site_id");

            migrationBuilder.CreateIndex(
                name: "ix_routers_tenant_id_name",
                table: "routers",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sites_slug",
                table: "sites",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_access_account_id_status",
                table: "subscriptions",
                columns: new[] { "access_account_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_package_id",
                table: "subscriptions",
                column: "package_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscriptions_tenant_id_source_source_reference",
                table: "subscriptions",
                columns: new[] { "tenant_id", "source", "source_reference" },
                unique: true,
                filter: "source_reference IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_tenants_slug",
                table: "tenants",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_tenant_id",
                table: "users",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_voucher_batches_package_id",
                table: "voucher_batches",
                column: "package_id");

            migrationBuilder.CreateIndex(
                name: "ix_vouchers_batch_id",
                table: "vouchers",
                column: "batch_id");

            migrationBuilder.CreateIndex(
                name: "ix_vouchers_tenant_id_code",
                table: "vouchers",
                columns: new[] { "tenant_id", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "access",
                schema: "radius");

            migrationBuilder.DropTable(
                name: "agents");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "background_jobs");

            migrationBuilder.DropTable(
                name: "customer_devices");

            migrationBuilder.DropTable(
                name: "ledger_entries");

            migrationBuilder.DropTable(
                name: "nas",
                schema: "radius");

            migrationBuilder.DropTable(
                name: "payment_callbacks");

            migrationBuilder.DropTable(
                name: "payments");

            migrationBuilder.DropTable(
                name: "radacct",
                schema: "radius");

            migrationBuilder.DropTable(
                name: "radpostauth",
                schema: "radius");

            migrationBuilder.DropTable(
                name: "router_events");

            migrationBuilder.DropTable(
                name: "router_health_samples");

            migrationBuilder.DropTable(
                name: "routers");

            migrationBuilder.DropTable(
                name: "subscriptions");

            migrationBuilder.DropTable(
                name: "support_notes");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "vouchers");

            migrationBuilder.DropTable(
                name: "access_accounts");

            migrationBuilder.DropTable(
                name: "tenants");

            migrationBuilder.DropTable(
                name: "voucher_batches");

            migrationBuilder.DropTable(
                name: "customers");

            migrationBuilder.DropTable(
                name: "packages");

            migrationBuilder.DropTable(
                name: "sites");
        }
    }
}
