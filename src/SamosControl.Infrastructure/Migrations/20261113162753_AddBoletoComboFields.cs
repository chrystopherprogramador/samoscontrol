using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SamosControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBoletoComboFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BoletoBarCode",
                table: "Charges",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BoletoPdfUrl",
                table: "Charges",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EfiBillet",
                table: "Charges",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BoletoBarCode",
                table: "Charges");

            migrationBuilder.DropColumn(
                name: "BoletoPdfUrl",
                table: "Charges");

            migrationBuilder.DropColumn(
                name: "EfiBillet",
                table: "Charges");
        }
    }
}
