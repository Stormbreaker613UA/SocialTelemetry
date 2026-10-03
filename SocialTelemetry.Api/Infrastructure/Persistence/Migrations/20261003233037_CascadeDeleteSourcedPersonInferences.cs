using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocialTelemetry.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CascadeDeleteSourcedPersonInferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PersonInferences_Interactions_SourceInteractionId",
                table: "PersonInferences");

            migrationBuilder.AddForeignKey(
                name: "FK_PersonInferences_Interactions_SourceInteractionId",
                table: "PersonInferences",
                column: "SourceInteractionId",
                principalTable: "Interactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PersonInferences_Interactions_SourceInteractionId",
                table: "PersonInferences");

            migrationBuilder.AddForeignKey(
                name: "FK_PersonInferences_Interactions_SourceInteractionId",
                table: "PersonInferences",
                column: "SourceInteractionId",
                principalTable: "Interactions",
                principalColumn: "Id");
        }
    }
}
