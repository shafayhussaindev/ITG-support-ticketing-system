using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupportTicketing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameAgentToStaff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TicketAssignments_Users_NewAgentId",
                table: "TicketAssignments");

            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_Users_AssignedAgentId",
                table: "Tickets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SatisfactionRatings_Agent",
                table: "SatisfactionRatings");

            migrationBuilder.RenameColumn(
                name: "AssignedAgentId",
                table: "Tickets",
                newName: "AssignedStaffId");

            migrationBuilder.RenameIndex(
                name: "IX_Tickets_Org_Agent_Status_Priority",
                table: "Tickets",
                newName: "IX_Tickets_Org_Staff_Status_Priority");

            migrationBuilder.RenameIndex(
                name: "IX_Tickets_AssignedAgentId",
                table: "Tickets",
                newName: "IX_Tickets_AssignedStaffId");

            migrationBuilder.RenameColumn(
                name: "PreviousAgentId",
                table: "TicketAssignments",
                newName: "PreviousStaffId");

            migrationBuilder.RenameColumn(
                name: "NewAgentId",
                table: "TicketAssignments",
                newName: "NewStaffId");

            migrationBuilder.RenameIndex(
                name: "IX_TicketAssignments_NewAgentId",
                table: "TicketAssignments",
                newName: "IX_TicketAssignments_NewStaffId");

            migrationBuilder.RenameColumn(
                name: "RatedAgentId",
                table: "SatisfactionRatings",
                newName: "RatedStaffId");

            migrationBuilder.RenameColumn(
                name: "AgentRating",
                table: "SatisfactionRatings",
                newName: "StaffRating");

            migrationBuilder.RenameIndex(
                name: "IX_SatisfactionRatings_Org_Agent_Submitted",
                table: "SatisfactionRatings",
                newName: "IX_SatisfactionRatings_Org_Staff_Submitted");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SatisfactionRatings_Staff",
                table: "SatisfactionRatings",
                sql: "[StaffRating] IS NULL OR [StaffRating] BETWEEN 1 AND 5");

            migrationBuilder.AddForeignKey(
                name: "FK_TicketAssignments_Users_NewStaffId",
                table: "TicketAssignments",
                column: "NewStaffId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_Users_AssignedStaffId",
                table: "Tickets",
                column: "AssignedStaffId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TicketAssignments_Users_NewStaffId",
                table: "TicketAssignments");

            migrationBuilder.DropForeignKey(
                name: "FK_Tickets_Users_AssignedStaffId",
                table: "Tickets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SatisfactionRatings_Staff",
                table: "SatisfactionRatings");

            migrationBuilder.RenameColumn(
                name: "AssignedStaffId",
                table: "Tickets",
                newName: "AssignedAgentId");

            migrationBuilder.RenameIndex(
                name: "IX_Tickets_Org_Staff_Status_Priority",
                table: "Tickets",
                newName: "IX_Tickets_Org_Agent_Status_Priority");

            migrationBuilder.RenameIndex(
                name: "IX_Tickets_AssignedStaffId",
                table: "Tickets",
                newName: "IX_Tickets_AssignedAgentId");

            migrationBuilder.RenameColumn(
                name: "PreviousStaffId",
                table: "TicketAssignments",
                newName: "PreviousAgentId");

            migrationBuilder.RenameColumn(
                name: "NewStaffId",
                table: "TicketAssignments",
                newName: "NewAgentId");

            migrationBuilder.RenameIndex(
                name: "IX_TicketAssignments_NewStaffId",
                table: "TicketAssignments",
                newName: "IX_TicketAssignments_NewAgentId");

            migrationBuilder.RenameColumn(
                name: "StaffRating",
                table: "SatisfactionRatings",
                newName: "AgentRating");

            migrationBuilder.RenameColumn(
                name: "RatedStaffId",
                table: "SatisfactionRatings",
                newName: "RatedAgentId");

            migrationBuilder.RenameIndex(
                name: "IX_SatisfactionRatings_Org_Staff_Submitted",
                table: "SatisfactionRatings",
                newName: "IX_SatisfactionRatings_Org_Agent_Submitted");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SatisfactionRatings_Agent",
                table: "SatisfactionRatings",
                sql: "[AgentRating] IS NULL OR [AgentRating] BETWEEN 1 AND 5");

            migrationBuilder.AddForeignKey(
                name: "FK_TicketAssignments_Users_NewAgentId",
                table: "TicketAssignments",
                column: "NewAgentId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tickets_Users_AssignedAgentId",
                table: "Tickets",
                column: "AssignedAgentId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
