using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocialTelemetry.Api.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddAttachmentTranscripts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AttachmentTranscripts",
                columns: table => new
                {
                    AttachmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GeneratedText = table.Column<string>(type: "TEXT", maxLength: 120000, nullable: true),
                    CorrectedText = table.Column<string>(type: "TEXT", maxLength: 120000, nullable: true),
                    ReviewStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    ReviewedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceStorageKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    TranscriptionVersion = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttachmentTranscripts", x => x.AttachmentId);
                    table.CheckConstraint("CK_AttachmentTranscripts_CorrectedText_Length", "length(\"CorrectedText\") <= 120000");
                    table.CheckConstraint("CK_AttachmentTranscripts_GeneratedText_Length", "length(\"GeneratedText\") <= 120000");
                    table.CheckConstraint("CK_AttachmentTranscripts_Model_Length", "length(\"Model\") <= 200");
                    table.CheckConstraint("CK_AttachmentTranscripts_Provider_Length", "length(\"Provider\") <= 200");
                    table.CheckConstraint("CK_AttachmentTranscripts_SourceSha256_Length", "length(\"SourceSha256\") <= 64");
                    table.CheckConstraint("CK_AttachmentTranscripts_SourceStorageKey_Length", "length(\"SourceStorageKey\") <= 32");
                    table.CheckConstraint("CK_AttachmentTranscripts_TranscriptionVersion_Length", "length(\"TranscriptionVersion\") <= 200");
                    table.ForeignKey(
                        name: "FK_AttachmentTranscripts_InteractionAttachments_AttachmentId",
                        column: x => x.AttachmentId,
                        principalTable: "InteractionAttachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttachmentTranscripts");
        }
    }
}
