using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeamerPresenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalPlaybackHistory : Migration
    {
        private static readonly string[] ExternalHistoryIndexColumns = ["ExternalSourceKey", "StartedUtc"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "MediaId",
                table: "PlaybackHistory",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<string>(
                name: "ExternalSourceKey",
                table: "PlaybackHistory",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackHistory_ExternalSourceKey_StartedUtc",
                table: "PlaybackHistory",
                columns: ExternalHistoryIndexColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlaybackHistory_ExternalSourceKey_StartedUtc",
                table: "PlaybackHistory");

            migrationBuilder.DropColumn(
                name: "ExternalSourceKey",
                table: "PlaybackHistory");

            migrationBuilder.AlterColumn<int>(
                name: "MediaId",
                table: "PlaybackHistory",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
