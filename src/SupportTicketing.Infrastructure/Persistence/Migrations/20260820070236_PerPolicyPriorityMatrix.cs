using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupportTicketing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PerPolicyPriorityMatrix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_PriorityMatrix_Org_Impact_Urgency",
                table: "PriorityMatrixEntries");

            migrationBuilder.AddColumn<Guid>(
                name: "SlaPolicyId",
                table: "PriorityMatrixEntries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriorityMatrixEntries_SlaPolicyId",
                table: "PriorityMatrixEntries",
                column: "SlaPolicyId");

            migrationBuilder.CreateIndex(
                name: "UX_PriorityMatrix_Org_Policy_Impact_Urgency",
                table: "PriorityMatrixEntries",
                columns: new[] { "OrganizationId", "SlaPolicyId", "Impact", "Urgency" },
                unique: true,
                filter: "[IsDeleted] = 0");

            migrationBuilder.AddForeignKey(
                name: "FK_PriorityMatrixEntries_SlaPolicies_SlaPolicyId",
                table: "PriorityMatrixEntries",
                column: "SlaPolicyId",
                principalTable: "SlaPolicies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PriorityMatrixEntries_SlaPolicies_SlaPolicyId",
                table: "PriorityMatrixEntries");

            migrationBuilder.DropIndex(
                name: "IX_PriorityMatrixEntries_SlaPolicyId",
                table: "PriorityMatrixEntries");

            migrationBuilder.DropIndex(
                name: "UX_PriorityMatrix_Org_Policy_Impact_Urgency",
                table: "PriorityMatrixEntries");

            migrationBuilder.DropColumn(
                name: "SlaPolicyId",
                table: "PriorityMatrixEntries");

            migrationBuilder.CreateIndex(
                name: "UX_PriorityMatrix_Org_Impact_Urgency",
                table: "PriorityMatrixEntries",
                columns: new[] { "OrganizationId", "Impact", "Urgency" },
                unique: true,
                filter: "[IsDeleted] = 0");
        }
    }
}
