using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeamerPresenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AudioChannels",
                table: "Videos",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudioCodec",
                table: "Videos",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Container",
                table: "Videos",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<TimeSpan>(
                name: "Duration",
                table: "Videos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "FrameRate",
                table: "Videos",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlaybackStatus",
                table: "Videos",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ProbeError",
                table: "Videos",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProbeStatus",
                table: "Videos",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "VideoCodec",
                table: "Videos",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VideoHeight",
                table: "Videos",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VideoWidth",
                table: "Videos",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AudioChannels",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "AudioCodec",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "Container",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "Duration",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "FrameRate",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "PlaybackStatus",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "ProbeError",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "ProbeStatus",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "VideoCodec",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "VideoHeight",
                table: "Videos");

            migrationBuilder.DropColumn(
                name: "VideoWidth",
                table: "Videos");
        }
    }
}
