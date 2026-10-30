using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SamosControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WhatsAppTemplatesPictures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PictureCheckedAt",
                table: "WhatsAppConversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PictureUrl",
                table: "WhatsAppConversations",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WaPictureUrl",
                table: "Sellers",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WhatsAppTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SellerId = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhatsAppTemplates", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WhatsAppTemplates");

            migrationBuilder.DropColumn(
                name: "PictureCheckedAt",
                table: "WhatsAppConversations");

            migrationBuilder.DropColumn(
                name: "PictureUrl",
                table: "WhatsAppConversations");

            migrationBuilder.DropColumn(
                name: "WaPictureUrl",
                table: "Sellers");
        }
    }
}
