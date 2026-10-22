using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocialTelemetry.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddLocalAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AccessGrantId",
                table: "LocalProfileSelection",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AccessEpoch",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "LocalProtection",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RecoveryFailures",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryHash",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RecoveryLockoutEnd",
                table: "AspNetUsers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LocalDeviceTicket",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProtectedTicket = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalDeviceTicket", x => x.Id);
                    table.CheckConstraint("CK_LocalDeviceTicket_TokenHash_Length", "length(\"TokenHash\") <= 64");
                });

            migrationBuilder.CreateTable(
                name: "LocalProfileGrant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SecurityEpoch = table.Column<Guid>(type: "TEXT", nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    HostInstance = table.Column<Guid>(type: "TEXT", nullable: false),
                    Remembered = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revoked = table.Column<bool>(type: "INTEGER", nullable: false),
                    SignedInAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastActivityAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalProfileGrant", x => x.Id);
                    table.CheckConstraint("CK_LocalProfileGrant_SecurityStamp_Length", "length(\"SecurityStamp\") <= 256");
                    table.ForeignKey(
                        name: "FK_LocalProfileGrant_AspNetUsers_AccountId",
                        column: x => x.AccountId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LocalProfileGrant_LocalDeviceTicket_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "LocalDeviceTicket",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocalDeviceTicket_TokenHash",
                table: "LocalDeviceTicket",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LocalProfileGrant_AccountId",
                table: "LocalProfileGrant",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LocalProfileGrant_DeviceId_AccountId",
                table: "LocalProfileGrant",
                columns: new[] { "DeviceId", "AccountId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocalProfileGrant");

            migrationBuilder.DropTable(
                name: "LocalDeviceTicket");

            migrationBuilder.DropColumn(
                name: "AccessGrantId",
                table: "LocalProfileSelection");

            migrationBuilder.DropColumn(
                name: "AccessEpoch",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "LocalProtection",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "RecoveryFailures",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "RecoveryHash",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "RecoveryLockoutEnd",
                table: "AspNetUsers");
        }
    }
}
