using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lakeflow.ControlApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class CommandResultDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResultDetails",
                table: "Commands",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Succeeded",
                table: "Commands",
                type: "bit",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResultDetails",
                table: "Commands");

            migrationBuilder.DropColumn(
                name: "Succeeded",
                table: "Commands");
        }
    }
}
