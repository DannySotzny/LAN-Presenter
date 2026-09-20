using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeamerPresenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPresenterDisplaySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AggressiveTopmost",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AlwaysOnTop",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "ChromePath",
                table: "Settings",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MonitorDeviceName",
                table: "Settings",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PreventDisplaySleep",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "PreventSystemSleep",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AggressiveTopmost",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "AlwaysOnTop",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ChromePath",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "MonitorDeviceName",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PreventDisplaySleep",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PreventSystemSleep",
                table: "Settings");
        }
    }
}
