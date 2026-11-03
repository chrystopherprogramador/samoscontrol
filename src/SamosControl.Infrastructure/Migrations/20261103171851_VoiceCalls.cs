using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SamosControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class VoiceCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VoiceAllowedNumbers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PhoneKey = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    ConsentBy = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ConsentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoiceAllowedNumbers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VoiceCalls",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Direction = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    State = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PhoneKey = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LeadId = table.Column<int>(type: "integer", nullable: true),
                    ContactName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ExternalCallId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    StartedByUserId = table.Column<int>(type: "integer", nullable: true),
                    StartedByName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ConnectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    EndReason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    FailureDetail = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    Outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Transcript = table.Column<string>(type: "text", nullable: true),
                    Turns = table.Column<int>(type: "integer", nullable: false),
                    BargeIns = table.Column<int>(type: "integer", nullable: false),
                    AvgResponseLatencyMs = table.Column<int>(type: "integer", nullable: false),
                    MaxResponseLatencyMs = table.Column<int>(type: "integer", nullable: false),
                    TtsCharacters = table.Column<int>(type: "integer", nullable: false),
                    SttSeconds = table.Column<int>(type: "integer", nullable: false),
                    LlmCalls = table.Column<int>(type: "integer", nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoiceCalls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VoiceCalls_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "VoiceCallEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VoiceCallId = table.Column<int>(type: "integer", nullable: false),
                    At = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoiceCallEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VoiceCallEvents_VoiceCalls_VoiceCallId",
                        column: x => x.VoiceCallId,
                        principalTable: "VoiceCalls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VoiceAllowedNumbers_PhoneKey",
                table: "VoiceAllowedNumbers",
                column: "PhoneKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VoiceCallEvents_VoiceCallId_At",
                table: "VoiceCallEvents",
                columns: new[] { "VoiceCallId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_VoiceCalls_ExternalCallId",
                table: "VoiceCalls",
                column: "ExternalCallId");

            migrationBuilder.CreateIndex(
                name: "IX_VoiceCalls_IdempotencyKey",
                table: "VoiceCalls",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VoiceCalls_LeadId",
                table: "VoiceCalls",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_VoiceCalls_PhoneKey_CreatedAt",
                table: "VoiceCalls",
                columns: new[] { "PhoneKey", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_VoiceCalls_PublicId",
                table: "VoiceCalls",
                column: "PublicId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VoiceAllowedNumbers");

            migrationBuilder.DropTable(
                name: "VoiceCallEvents");

            migrationBuilder.DropTable(
                name: "VoiceCalls");
        }
    }
}
