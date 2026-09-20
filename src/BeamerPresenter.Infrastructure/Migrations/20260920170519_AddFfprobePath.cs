using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeamerPresenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFfprobePath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FfprobePath",
                table: "Settings",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FfprobePath",
                table: "Settings");
        }
    }
}
