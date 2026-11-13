using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SamosControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CentralInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AssignedSellerId",
                table: "WhatsAppConversations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Resolved",
                table: "WhatsAppConversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppConversations_AssignedSellerId_Resolved",
                table: "WhatsAppConversations",
                columns: new[] { "AssignedSellerId", "Resolved" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WhatsAppConversations_AssignedSellerId_Resolved",
                table: "WhatsAppConversations");

            migrationBuilder.DropColumn(
                name: "AssignedSellerId",
                table: "WhatsAppConversations");

            migrationBuilder.DropColumn(
                name: "Resolved",
                table: "WhatsAppConversations");
        }
    }
}
