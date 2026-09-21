using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BeamerPresenter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Enabled",
                table: "Videos",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Enabled",
                table: "Videos");
        }
    }
}
