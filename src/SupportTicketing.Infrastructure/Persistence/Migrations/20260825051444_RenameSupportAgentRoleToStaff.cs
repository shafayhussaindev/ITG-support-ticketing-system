using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupportTicketing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameSupportAgentRoleToStaff : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// The role row itself, not just the code constant. The seeder matches system
        /// roles by name, so renaming the constant alone would have created a second
        /// role called Staff on the next startup and left every existing Support Agent
        /// stranded on an orphaned row.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guarded on a name that is not already taken. An installation that has
            // hand-made a role called Staff must be reconciled by a person, not by a
            // migration silently merging two sets of permissions.
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM Roles WHERE Name = 'Staff' AND IsDeleted = 0)
                    UPDATE Roles
                    SET Name = 'Staff',
                        Description = CASE
                            WHEN Description = 'System role: Support Agent.' THEN 'System role: Staff.'
                            ELSE Description
                        END
                    WHERE Name = 'Support Agent';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM Roles WHERE Name = 'Support Agent' AND IsDeleted = 0)
                    UPDATE Roles
                    SET Name = 'Support Agent',
                        Description = CASE
                            WHEN Description = 'System role: Staff.' THEN 'System role: Support Agent.'
                            ELSE Description
                        END
                    WHERE Name = 'Staff';
                """);
        }
    }
}
