using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeamerPresenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaybackQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClipLengthMaxSeconds",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 600);

            migrationBuilder.AddColumn<int>(
                name: "ClipLengthMinSeconds",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 420);

            migrationBuilder.AddColumn<int>(
                name: "QueueTargetLength",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<int>(
                name: "ShortVideoThresholdSeconds",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 600);

            migrationBuilder.AddColumn<int>(
                name: "TimeCooldownMinutes",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<int>(
                name: "VideoCooldownCount",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.CreateTable(
                name: "PlaybackHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MediaId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceType = table.Column<int>(type: "INTEGER", nullable: false),
                    PlannedStart = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    PlannedEnd = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    ActualStart = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    ActualEnd = table.Column<TimeSpan>(type: "TEXT", nullable: true),
                    StartedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Interrupted = table.Column<bool>(type: "INTEGER", nullable: false),
                    PlaybackReason = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybackHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QueueEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceType = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaId = table.Column<int>(type: "INTEGER", nullable: true),
                    ExternalSourceKey = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    StartPosition = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    EndPosition = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    Origin = table.Column<int>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QueueEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackHistory_MediaId_StartedUtc",
                table: "PlaybackHistory",
                columns: new[] { "MediaId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_QueueEntries_Status_SortOrder",
                table: "QueueEntries",
                columns: new[] { "Status", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaybackHistory");

            migrationBuilder.DropTable(
                name: "QueueEntries");

            migrationBuilder.DropColumn(
                name: "ClipLengthMaxSeconds",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ClipLengthMinSeconds",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "QueueTargetLength",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "ShortVideoThresholdSeconds",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "TimeCooldownMinutes",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "VideoCooldownCount",
                table: "Settings");
        }
    }
}
