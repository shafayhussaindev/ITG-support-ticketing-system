using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupportTicketing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FixSystemSettingsUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_SystemSettings_Org_Key",
                table: "SystemSettings");

            migrationBuilder.CreateIndex(
                name: "UX_SystemSettings_Org_Key",
                table: "SystemSettings",
                columns: new[] { "OrganizationId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_SystemSettings_Org_Key",
                table: "SystemSettings");

            migrationBuilder.CreateIndex(
                name: "UX_SystemSettings_Org_Key",
                table: "SystemSettings",
                columns: new[] { "OrganizationId", "Key" },
                unique: true,
                filter: "[OrganizationId] IS NOT NULL");
        }
    }
}
