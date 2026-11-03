using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SamosControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RobertaSalesFeeling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Format",
                table: "SdrSuggestions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AudioSentCount",
                table: "ConversationStates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "BuyingIntent",
                table: "ConversationStates",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "baixa");

            migrationBuilder.AddColumn<bool>(
                name: "ClientSentAudio",
                table: "ConversationStates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ClientStyle",
                table: "ConversationStates",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentGoal",
                table: "ConversationStates",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Engagement",
                table: "ConversationStates",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "baixo");

            migrationBuilder.AddColumn<string>(
                name: "FeaturesPresented",
                table: "ConversationStates",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FollowUpCount",
                table: "ConversationStates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "HandoffKind",
                table: "ConversationStates",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InboundWords",
                table: "ConversationStates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastFormat",
                table: "ConversationStates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastInboundAt",
                table: "ConversationStates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastQuestion",
                table: "ConversationStates",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MainPain",
                table: "ConversationStates",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PainDepth",
                table: "ConversationStates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PainIntensity",
                table: "ConversationStates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PitchCount",
                table: "ConversationStates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ReadyForHandoff",
                table: "ConversationStates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Temperature",
                table: "ConversationStates",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Format",
                table: "SdrSuggestions");

            migrationBuilder.DropColumn(
                name: "AudioSentCount",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "BuyingIntent",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "ClientSentAudio",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "ClientStyle",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "CurrentGoal",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "Engagement",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "FeaturesPresented",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "FollowUpCount",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "HandoffKind",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "InboundWords",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "LastFormat",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "LastInboundAt",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "LastQuestion",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "MainPain",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "PainDepth",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "PainIntensity",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "PitchCount",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "ReadyForHandoff",
                table: "ConversationStates");

            migrationBuilder.DropColumn(
                name: "Temperature",
                table: "ConversationStates");
        }
    }
}
