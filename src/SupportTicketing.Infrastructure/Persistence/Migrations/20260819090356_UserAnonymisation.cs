using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SupportTicketing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserAnonymisation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AnonymisedAtUtc",
                table: "Users",
                type: "datetime2(3)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAnonymised",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnonymisedAtUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsAnonymised",
                table: "Users");
        }
    }
}
