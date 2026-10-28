using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lakeflow.ControlApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentEvents",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SiteId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OccurredUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Json = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentEvents", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Entity = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Before = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    After = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LogBundles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CommandId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReceivedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Zip = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogBundles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sites",
                columns: table => new
                {
                    SiteId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TimeZone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Latitude = table.Column<double>(type: "float", nullable: true),
                    Longitude = table.Column<double>(type: "float", nullable: true),
                    MaintenanceStart = table.Column<TimeOnly>(type: "time", nullable: true),
                    MaintenanceEnd = table.Column<TimeOnly>(type: "time", nullable: true),
                    DesiredAgentVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    BufferCapBytes = table.Column<long>(type: "bigint", nullable: false),
                    ConfigVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sites", x => x.SiteId);
                });

            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    AgentId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SiteId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Hostname = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    AgentVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    EnrolledUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CertificateThumbprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CertificatePem = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CertificateExpiresUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PreviousThumbprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RevokedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LastHeartbeatUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastHeartbeatJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SasExpiresUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.AgentId);
                    table.ForeignKey(
                        name: "FK_Agents_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "SiteId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EnrollmentTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TokenHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UsedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UsedByAgentId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnrollmentTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EnrollmentTokens_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "SiteId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sources",
                columns: table => new
                {
                    SiteId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceSystem = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Division = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Engine = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Host = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    Port = table.Column<int>(type: "int", nullable: true),
                    Instance = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DatabaseName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Auth = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CredentialName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ConnectTimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    CommandTimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    TrustServerCertificate = table.Column<bool>(type: "bit", nullable: false),
                    Paused = table.Column<bool>(type: "bit", nullable: false),
                    MaxRowsPerSecond = table.Column<int>(type: "int", nullable: false),
                    CredentialStoredUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sources", x => new { x.SiteId, x.SourceId });
                    table.ForeignKey(
                        name: "FK_Sources_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "SiteId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Commands",
                columns: table => new
                {
                    CommandId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TableName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: true),
                    IssuedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    IssuedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DeliveredUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Result = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Commands", x => x.CommandId);
                    table.ForeignKey(
                        name: "FK_Commands_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "AgentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SealedCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SiteId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CredentialName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Sealed = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SealedCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SealedCredentials_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "AgentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tables",
                columns: table => new
                {
                    SiteId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    KeyColumns = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    WatermarkColumn = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LookbackSeconds = table.Column<int>(type: "int", nullable: false),
                    PollIntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    LagWarningMinutes = table.Column<int>(type: "int", nullable: false),
                    LagCriticalMinutes = table.Column<int>(type: "int", nullable: false),
                    Paused = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tables", x => new { x.SiteId, x.SourceId, x.TableName });
                    table.ForeignKey(
                        name: "FK_Tables_Sources_SiteId_SourceId",
                        columns: x => new { x.SiteId, x.SourceId },
                        principalTable: "Sources",
                        principalColumns: new[] { "SiteId", "SourceId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentEvents_SiteId_ReceivedUtc",
                table: "AgentEvents",
                columns: new[] { "SiteId", "ReceivedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_CertificateThumbprint",
                table: "Agents",
                column: "CertificateThumbprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Agents_SiteId",
                table: "Agents",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_AtUtc",
                table: "AuditEntries",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Commands_AgentId_CompletedUtc",
                table: "Commands",
                columns: new[] { "AgentId", "CompletedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EnrollmentTokens_SiteId",
                table: "EnrollmentTokens",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_EnrollmentTokens_TokenHash",
                table: "EnrollmentTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SealedCredentials_AgentId",
                table: "SealedCredentials",
                column: "AgentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentEvents");

            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "Commands");

            migrationBuilder.DropTable(
                name: "EnrollmentTokens");

            migrationBuilder.DropTable(
                name: "LogBundles");

            migrationBuilder.DropTable(
                name: "SealedCredentials");

            migrationBuilder.DropTable(
                name: "Tables");

            migrationBuilder.DropTable(
                name: "Agents");

            migrationBuilder.DropTable(
                name: "Sources");

            migrationBuilder.DropTable(
                name: "Sites");
        }
    }
}
