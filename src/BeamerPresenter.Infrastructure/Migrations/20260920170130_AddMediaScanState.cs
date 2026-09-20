using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeamerPresenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaScanState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "FullPath",
                table: "Videos",
                type: "TEXT",
                maxLength: 4096,
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 4096);

            migrationBuilder.AddColumn<bool>(
                name: "IsAvailable",
                table: "Videos",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastScannedUtc",
                table: "Videos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastWriteUtc",
                table: "Videos",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsAvailable",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "LastScannedUtc",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "LastWriteUtc",
                table: "Videos");

            migrationBuilder.AlterColumn<string>(
                name: "FullPath",
                table: "Videos",
                type: "TEXT",
                maxLength: 4096,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 4096,
                oldCollation: "NOCASE");
        }
    }
}
