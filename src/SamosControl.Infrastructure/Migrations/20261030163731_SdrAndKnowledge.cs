using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SamosControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SdrAndKnowledge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Regions",
                table: "Sellers",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiKnowledge",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Topic = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Question = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    Answer = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Origin = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LeadId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiKnowledge", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SdrCampaigns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SellerId = table.Column<int>(type: "integer", nullable: true),
                    DailyLimit = table.Column<int>(type: "integer", nullable: false),
                    StartHour = table.Column<int>(type: "integer", nullable: false),
                    EndHour = table.Column<int>(type: "integer", nullable: false),
                    MinDelaySec = table.Column<int>(type: "integer", nullable: false),
                    MaxDelaySec = table.Column<int>(type: "integer", nullable: false),
                    Variants = table.Column<string>(type: "character varying(6000)", maxLength: 6000, nullable: false),
                    FollowUpText = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    FollowUpHours = table.Column<int>(type: "integer", nullable: false),
                    MaxFollowUps = table.Column<int>(type: "integer", nullable: false),
                    Pitch = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AiReplies = table.Column<bool>(type: "boolean", nullable: false),
                    HandoffAlertMinutes = table.Column<int>(type: "integer", nullable: false),
                    FilterStatus = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    FilterState = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    FilterSegment = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    NextSendAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SdrCampaigns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SdrContacts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CampaignId = table.Column<int>(type: "integer", nullable: false),
                    LeadId = table.Column<int>(type: "integer", nullable: false),
                    Phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PhoneKey = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Stage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Variant = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    AiTurns = table.Column<int>(type: "integer", nullable: false),
                    LastSentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextActionAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RepliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Score = table.Column<int>(type: "integer", nullable: false),
                    Summary = table.Column<string>(type: "character varying(1500)", maxLength: 1500, nullable: true),
                    HandoffSellerId = table.Column<int>(type: "integer", nullable: true),
                    HandoffAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Alerted = table.Column<bool>(type: "boolean", nullable: false),
                    Error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SdrContacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SdrContacts_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SdrContacts_SdrCampaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "SdrCampaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiKnowledge_Status",
                table: "AiKnowledge",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SdrContacts_CampaignId_LeadId",
                table: "SdrContacts",
                columns: new[] { "CampaignId", "LeadId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SdrContacts_CampaignId_Stage_NextActionAt",
                table: "SdrContacts",
                columns: new[] { "CampaignId", "Stage", "NextActionAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SdrContacts_LeadId",
                table: "SdrContacts",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_SdrContacts_PhoneKey",
                table: "SdrContacts",
                column: "PhoneKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiKnowledge");

            migrationBuilder.DropTable(
                name: "SdrContacts");

            migrationBuilder.DropTable(
                name: "SdrCampaigns");

            migrationBuilder.DropColumn(
                name: "Regions",
                table: "Sellers");
        }
    }
}
