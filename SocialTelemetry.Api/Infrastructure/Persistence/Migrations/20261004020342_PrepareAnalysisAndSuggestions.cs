using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocialTelemetry.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PrepareAnalysisAndSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "InteractionAnalyses",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "InteractionAnalyses",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResultJson",
                table: "InteractionAnalyses",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SchemaVersion",
                table: "InteractionAnalyses",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SuggestedProfileUpdates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    InteractionAnalysisId = table.Column<Guid>(type: "uuid", nullable: false),
                    Field = table.Column<string>(type: "text", nullable: false),
                    SuggestedValue = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AcceptedValue = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SuggestedProfileUpdates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SuggestedProfileUpdates_InteractionAnalyses_InteractionAnal~",
                        column: x => x.InteractionAnalysisId,
                        principalTable: "InteractionAnalyses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SuggestedProfileUpdates_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SuggestedProfileUpdates_InteractionAnalysisId",
                table: "SuggestedProfileUpdates",
                column: "InteractionAnalysisId");

            migrationBuilder.CreateIndex(
                name: "IX_SuggestedProfileUpdates_PersonId",
                table: "SuggestedProfileUpdates",
                column: "PersonId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SuggestedProfileUpdates");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "InteractionAnalyses");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "InteractionAnalyses");

            migrationBuilder.DropColumn(
                name: "ResultJson",
                table: "InteractionAnalyses");

            migrationBuilder.DropColumn(
                name: "SchemaVersion",
                table: "InteractionAnalyses");
        }
    }
}
