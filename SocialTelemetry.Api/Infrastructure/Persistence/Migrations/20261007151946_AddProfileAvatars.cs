using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocialTelemetry.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileAvatars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvatarMimeType",
                table: "UserProfiles",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AvatarStorageKey",
                table: "UserProfiles",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AvatarMimeType",
                table: "People",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AvatarStorageKey",
                table: "People",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvatarMimeType",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "AvatarStorageKey",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "AvatarMimeType",
                table: "People");

            migrationBuilder.DropColumn(
                name: "AvatarStorageKey",
                table: "People");
        }
    }
}
