using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SamosControl.Infrastructure.Migrations
{
    [DbContext(typeof(AppDbContext))]
    [Migration("20261113120000_ChargeExternalRef")]
    public partial class ChargeExternalRef : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalRef",
                table: "Charges",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ExternalRef", table: "Charges");
        }
    }
}
