using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupportTicketing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClaimedSeverity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClaimedImpact",
                table: "Tickets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClaimedUrgency",
                table: "Tickets",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimedImpact",
                table: "Tickets");

            migrationBuilder.DropColumn(
                name: "ClaimedUrgency",
                table: "Tickets");
        }
    }
}
