using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SamosControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RobertaV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "SdrCampaigns",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Shadow");

            migrationBuilder.CreateTable(
                name: "ConversationStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LeadId = table.Column<int>(type: "integer", nullable: false),
                    Stage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    InterestLevel = table.Column<int>(type: "integer", nullable: false),
                    StoreCount = table.Column<int>(type: "integer", nullable: true),
                    CurrentErp = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Segment = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    PainPoints = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Objections = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    QuestionsAsked = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    QuestionsAnswered = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    PriceDiscussed = table.Column<bool>(type: "boolean", nullable: false),
                    DemoOffered = table.Column<bool>(type: "boolean", nullable: false),
                    DemoAccepted = table.Column<bool>(type: "boolean", nullable: false),
                    HumanRequested = table.Column<bool>(type: "boolean", nullable: false),
                    OptOut = table.Column<bool>(type: "boolean", nullable: false),
                    SatisfiedWithCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    NegativeStreak = table.Column<int>(type: "integer", nullable: false),
                    TurnCount = table.Column<int>(type: "integer", nullable: false),
                    LastAction = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    NextAction = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Summary = table.Column<string>(type: "character varying(1500)", maxLength: 1500, nullable: true),
                    LastContactAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationStates_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LlmExecutions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LeadId = table.Column<int>(type: "integer", nullable: true),
                    Task = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    PromptName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    PromptVersion = table.Column<int>(type: "integer", nullable: true),
                    Model = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Input = table.Column<string>(type: "text", nullable: true),
                    Output = table.Column<string>(type: "text", nullable: true),
                    LatencyMs = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ValidatorResult = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlmExecutions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PromptVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PassRate = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptVersions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SdrSuggestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContactId = table.Column<int>(type: "integer", nullable: false),
                    LeadId = table.Column<int>(type: "integer", nullable: false),
                    InboundText = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Reply = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    EditedReply = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ValidatorOk = table.Column<bool>(type: "boolean", nullable: false),
                    ValidatorIssues = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ContextJson = table.Column<string>(type: "text", nullable: true),
                    ScoresJson = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ReviewedBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PromptVersion = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SdrSuggestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SdrSuggestions_SdrContacts_ContactId",
                        column: x => x.ContactId,
                        principalTable: "SdrContacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationStates_LeadId",
                table: "ConversationStates",
                column: "LeadId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LlmExecutions_CreatedAt",
                table: "LlmExecutions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PromptVersions_Name_Version",
                table: "PromptVersions",
                columns: new[] { "Name", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SdrSuggestions_ContactId",
                table: "SdrSuggestions",
                column: "ContactId");

            migrationBuilder.CreateIndex(
                name: "IX_SdrSuggestions_Status_CreatedAt",
                table: "SdrSuggestions",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationStates");

            migrationBuilder.DropTable(
                name: "LlmExecutions");

            migrationBuilder.DropTable(
                name: "PromptVersions");

            migrationBuilder.DropTable(
                name: "SdrSuggestions");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "SdrCampaigns");
        }
    }
}
