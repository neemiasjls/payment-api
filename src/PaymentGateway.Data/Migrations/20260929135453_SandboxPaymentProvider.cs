using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentGateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class SandboxPaymentProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastOperationError",
                table: "Payments",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingOperation",
                table: "Payments",
                type: "TEXT",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "Payments",
                type: "TEXT",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ProviderPaymentId",
                table: "Payments",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Payments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "DeliveryLeaseToken",
                table: "PaymentEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveryLeaseUntilUtc",
                table: "PaymentEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DeliverySkippedAtUtc",
                table: "PaymentEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextDeliveryAttemptAtUtc",
                table: "PaymentEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Operation",
                table: "IdempotencyRecords",
                type: "TEXT",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "IdempotencyRecords",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "ProviderWebhookEvents",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ProviderPaymentId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ProcessedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderWebhookEvents", x => new { x.Provider, x.EventId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Provider_ProviderPaymentId",
                table: "Payments",
                columns: new[] { "Provider", "ProviderPaymentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentEvents_DeliveredAtUtc_DeliverySkippedAtUtc_NextDeliveryAttemptAtUtc_DeliveryLeaseUntilUtc",
                table: "PaymentEvents",
                columns: new[] { "DeliveredAtUtc", "DeliverySkippedAtUtc", "NextDeliveryAttemptAtUtc", "DeliveryLeaseUntilUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderWebhookEvents");

            migrationBuilder.DropIndex(
                name: "IX_Payments_Provider_ProviderPaymentId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_PaymentEvents_DeliveredAtUtc_DeliverySkippedAtUtc_NextDeliveryAttemptAtUtc_DeliveryLeaseUntilUtc",
                table: "PaymentEvents");

            migrationBuilder.DropColumn(
                name: "LastOperationError",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PendingOperation",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ProviderPaymentId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "DeliveryLeaseToken",
                table: "PaymentEvents");

            migrationBuilder.DropColumn(
                name: "DeliveryLeaseUntilUtc",
                table: "PaymentEvents");

            migrationBuilder.DropColumn(
                name: "DeliverySkippedAtUtc",
                table: "PaymentEvents");

            migrationBuilder.DropColumn(
                name: "NextDeliveryAttemptAtUtc",
                table: "PaymentEvents");

            migrationBuilder.DropColumn(
                name: "Operation",
                table: "IdempotencyRecords");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "IdempotencyRecords");
        }
    }
}
