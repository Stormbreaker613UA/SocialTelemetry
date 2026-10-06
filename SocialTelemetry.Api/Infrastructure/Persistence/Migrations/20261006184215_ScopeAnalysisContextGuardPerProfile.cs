using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocialTelemetry.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopeAnalysisContextGuardPerProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Guard rows carry no business data. Replace them rather than convert a singleton key.
            migrationBuilder.DropTable(name: "AnalysisContextGuards");
            migrationBuilder.CreateTable(
                name: "AnalysisContextGuards",
                columns: table => new
                {
                    UserProfileId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_AnalysisContextGuards", x => x.UserProfileId));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AnalysisContextGuards");
            migrationBuilder.CreateTable(
                name: "AnalysisContextGuards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_AnalysisContextGuards", x => x.Id));

            migrationBuilder.InsertData(
                table: "AnalysisContextGuards",
                column: "Id",
                value: 1);
        }
    }
}
