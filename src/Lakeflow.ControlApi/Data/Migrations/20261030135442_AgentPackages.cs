using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lakeflow.ControlApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Platform",
                table: "Agents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdateJson",
                table: "Agents",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgentPackages",
                columns: table => new
                {
                    Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    UploadedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UploadedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentPackages", x => x.Version);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentPackages");

            migrationBuilder.DropColumn(
                name: "Platform",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "UpdateJson",
                table: "Agents");
        }
    }
}
