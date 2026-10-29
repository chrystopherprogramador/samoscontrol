using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SamosControl.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ContractsFinancialAndOtp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VerificationMethod",
                table: "ContractSignatures",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VerifiedAt",
                table: "ContractSignatures",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VerifiedPhone",
                table: "ContractSignatures",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BillingClosed",
                table: "Contracts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "BillingType",
                table: "Contracts",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Pix");

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountValue",
                table: "Contracts",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "ImplementationFirstDue",
                table: "Contracts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImplementationInstallments",
                table: "Contracts",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<decimal>(
                name: "ImplementationValue",
                table: "Contracts",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "MonthlyMonths",
                table: "Contracts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MonthlyStart",
                table: "Contracts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MonthlyValue",
                table: "Contracts",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "VerificationCode",
                table: "Contracts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OtpAttempts",
                table: "ContractParties",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "OtpExpiresAt",
                table: "ContractParties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OtpHash",
                table: "ContractParties",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OtpSentAt",
                table: "ContractParties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Charges",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "AVULSA");

            migrationBuilder.AddColumn<int>(
                name: "Sequence",
                table: "Charges",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Contracts_VerificationCode",
                table: "Contracts",
                column: "VerificationCode",
                unique: true,
                filter: "\"VerificationCode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Charges_ContractId_Kind_Sequence",
                table: "Charges",
                columns: new[] { "ContractId", "Kind", "Sequence" },
                unique: true,
                filter: "\"Kind\" <> 'AVULSA'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Contracts_VerificationCode",
                table: "Contracts");

            migrationBuilder.DropIndex(
                name: "IX_Charges_ContractId_Kind_Sequence",
                table: "Charges");

            migrationBuilder.DropColumn(
                name: "VerificationMethod",
                table: "ContractSignatures");

            migrationBuilder.DropColumn(
                name: "VerifiedAt",
                table: "ContractSignatures");

            migrationBuilder.DropColumn(
                name: "VerifiedPhone",
                table: "ContractSignatures");

            migrationBuilder.DropColumn(
                name: "BillingClosed",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "BillingType",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "DiscountValue",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "ImplementationFirstDue",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "ImplementationInstallments",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "ImplementationValue",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "MonthlyMonths",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "MonthlyStart",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "MonthlyValue",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "VerificationCode",
                table: "Contracts");

            migrationBuilder.DropColumn(
                name: "OtpAttempts",
                table: "ContractParties");

            migrationBuilder.DropColumn(
                name: "OtpExpiresAt",
                table: "ContractParties");

            migrationBuilder.DropColumn(
                name: "OtpHash",
                table: "ContractParties");

            migrationBuilder.DropColumn(
                name: "OtpSentAt",
                table: "ContractParties");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Charges");

            migrationBuilder.DropColumn(
                name: "Sequence",
                table: "Charges");
        }
    }
}
