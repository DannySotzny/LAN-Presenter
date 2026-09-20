using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeamerPresenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MediaFolders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Path = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false, collation: "NOCASE"),
                    IncludeSubdirectories = table.Column<bool>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaFolders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaFolders_Path",
                table: "MediaFolders",
                column: "Path",
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO "MediaFolders" ("Path", "IncludeSubdirectories", "Enabled", "CreatedUtc")
                SELECT "MediaFolder", 1, 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                FROM "Settings"
                WHERE trim("MediaFolder") <> '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaFolders");
        }
    }
}
