using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeamerPresenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddYouTubeDownloadSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "YouTubeSourceKey",
                table: "Videos",
                type: "TEXT",
                maxLength: 19,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Videos_YouTubeSourceKey",
                table: "Videos",
                column: "YouTubeSourceKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Videos_YouTubeSourceKey",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "YouTubeSourceKey",
                table: "Videos");
        }
    }
}
