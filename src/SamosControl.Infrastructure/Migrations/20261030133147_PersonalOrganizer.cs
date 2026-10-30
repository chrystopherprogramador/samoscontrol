using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SamosControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PersonalOrganizer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PersonalExpenses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    IsIncome = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Category = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalExpenses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PersonalNotes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Tag = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalNotes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PersonalTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Category = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    DueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RemindAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RepeatMinutes = table.Column<int>(type: "integer", nullable: true),
                    Done = table.Column<bool>(type: "boolean", nullable: false),
                    DoneAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalTasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PersonalExpenses_UserId_Date",
                table: "PersonalExpenses",
                columns: new[] { "UserId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_PersonalNotes_UserId",
                table: "PersonalNotes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_PersonalTasks_RemindAt",
                table: "PersonalTasks",
                column: "RemindAt");

            migrationBuilder.CreateIndex(
                name: "IX_PersonalTasks_UserId_Done",
                table: "PersonalTasks",
                columns: new[] { "UserId", "Done" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PersonalExpenses");

            migrationBuilder.DropTable(
                name: "PersonalNotes");

            migrationBuilder.DropTable(
                name: "PersonalTasks");
        }
    }
}
