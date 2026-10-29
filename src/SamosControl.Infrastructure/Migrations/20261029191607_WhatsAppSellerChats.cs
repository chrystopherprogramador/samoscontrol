using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SamosControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WhatsAppSellerChats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WhatsAppMessages_ExternalId",
                table: "WhatsAppMessages");

            migrationBuilder.DropIndex(
                name: "IX_WhatsAppConversations_Phone",
                table: "WhatsAppConversations");

            migrationBuilder.AddColumn<string>(
                name: "LastMessagePreview",
                table: "WhatsAppConversations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SellerId",
                table: "WhatsAppConversations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WaConnectedAt",
                table: "Sellers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WaInstance",
                table: "Sellers",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WaNumber",
                table: "Sellers",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WaProfileName",
                table: "Sellers",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WaStatus",
                table: "Sellers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "DESCONECTADO");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppMessages_ConversationId_ExternalId",
                table: "WhatsAppMessages",
                columns: new[] { "ConversationId", "ExternalId" },
                unique: true,
                filter: "\"ExternalId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppMessages_ExternalId",
                table: "WhatsAppMessages",
                column: "ExternalId");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppConversations_Phone",
                table: "WhatsAppConversations",
                column: "Phone",
                unique: true,
                filter: "\"SellerId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppConversations_SellerId_Phone",
                table: "WhatsAppConversations",
                columns: new[] { "SellerId", "Phone" },
                unique: true,
                filter: "\"SellerId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Sellers_WaInstance",
                table: "Sellers",
                column: "WaInstance",
                unique: true,
                filter: "\"WaInstance\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WhatsAppMessages_ConversationId_ExternalId",
                table: "WhatsAppMessages");

            migrationBuilder.DropIndex(
                name: "IX_WhatsAppMessages_ExternalId",
                table: "WhatsAppMessages");

            migrationBuilder.DropIndex(
                name: "IX_WhatsAppConversations_Phone",
                table: "WhatsAppConversations");

            migrationBuilder.DropIndex(
                name: "IX_WhatsAppConversations_SellerId_Phone",
                table: "WhatsAppConversations");

            migrationBuilder.DropIndex(
                name: "IX_Sellers_WaInstance",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "LastMessagePreview",
                table: "WhatsAppConversations");

            migrationBuilder.DropColumn(
                name: "SellerId",
                table: "WhatsAppConversations");

            migrationBuilder.DropColumn(
                name: "WaConnectedAt",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "WaInstance",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "WaNumber",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "WaProfileName",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "WaStatus",
                table: "Sellers");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppMessages_ExternalId",
                table: "WhatsAppMessages",
                column: "ExternalId",
                unique: true,
                filter: "\"ExternalId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppConversations_Phone",
                table: "WhatsAppConversations",
                column: "Phone",
                unique: true);
        }
    }
}
