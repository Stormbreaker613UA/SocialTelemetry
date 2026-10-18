using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocialTelemetry.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLocalProfileSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LocalProfileSelection",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    LegacySingleProfileAccess = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalProfileSelection", x => x.Id);
                    table.CheckConstraint("CK_LocalProfileSelection_Singleton", "\"Id\" = 1");
                    table.ForeignKey(
                        name: "FK_LocalProfileSelection_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.NoAction);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LocalProfileSelection_UserProfileId",
                table: "LocalProfileSelection",
                column: "UserProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LocalProfileSelection");
        }
    }
}
