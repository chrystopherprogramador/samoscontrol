using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SamosControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Roberta4 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ObjectionKind",
                table: "SdrSuggestions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Strategy",
                table: "SdrSuggestions",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActionHistory",
                table: "ConversationStates",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AiDisclosed",
                table: "ConversationStates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Commitments",
                table: "ConversationStates",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Hypotheses",
                table: "ConversationStates",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ImpactKnown",
                table: "ConversationStates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ImpactNotes",
                table: "ConversationStates",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastInboundHash",
                table: "ConversationStates",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LossSignals",
                table: "ConversationStates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ObjectionLog",
                table: "ConversationStates",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OpportunityStage",
                table: "ConversationStates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "descoberta");

            migrationBuilder.AddColumn<string>(
                name: "PainCategories",
                table: "ConversationStates",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredChannel",
                table: "ConversationStates",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredTime",
                table: "ConversationStates",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StateVersion",
                table: "ConversationStates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Strategy",
                table: "ConversationStates",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ShadowConversationStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LeadId = table.Column<int>(type: "integer", nullable: false),
                    Json = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShadowConversationStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShadowConversationStates_LeadId",
                table: "ShadowConversationStates",
                column: "LeadId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShadowConversationStates");

            migrationBuilder.DropColumn(
                name: "ObjectionKind",
                table: "SdrSuggestions");

            migrationBuilder.DropColumn(
                name: "Strategy",
                table: "SdrSuggestions");

            migrationBuilder.DropColumn(
                name: "ActionHistory",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "AiDisclosed",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "Commitments",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "Hypotheses",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "ImpactKnown",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "ImpactNotes",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "LastInboundHash",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "LossSignals",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "ObjectionLog",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "OpportunityStage",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "PainCategories",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "PreferredChannel",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "PreferredTime",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "StateVersion",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "Strategy",
                table: "ConversationStates");
        }
    }
}
