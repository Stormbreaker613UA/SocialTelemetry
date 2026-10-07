using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocialTelemetry.Api.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class InitialSqlite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnalysisContextGuards",
                columns: table => new
                {
                    UserProfileId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisContextGuards", x => x.UserProfileId);
                });

            migrationBuilder.CreateTable(
                name: "UserProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    AboutMe = table.Column<string>(type: "TEXT", nullable: true),
                    CommunicationStyle = table.Column<string>(type: "TEXT", nullable: true),
                    Goals = table.Column<string>(type: "TEXT", nullable: true),
                    Preferences = table.Column<string>(type: "TEXT", nullable: true),
                    Boundaries = table.Column<string>(type: "TEXT", nullable: true),
                    AiInstructions = table.Column<string>(type: "TEXT", nullable: true),
                    AvatarStorageKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    AvatarMimeType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.Id);
                    table.CheckConstraint("CK_UserProfiles_AvatarMimeType_Length", "length(\"AvatarMimeType\") <= 32");
                    table.CheckConstraint("CK_UserProfiles_AvatarStorageKey_Length", "length(\"AvatarStorageKey\") <= 32");
                });

            migrationBuilder.CreateTable(
                name: "Interactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    UserThoughts = table.Column<string>(type: "TEXT", nullable: true),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Interactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Interactions_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "People",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Age = table.Column<int>(type: "INTEGER", nullable: true),
                    Gender = table.Column<string>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    RelationshipContext = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    HowWeMet = table.Column<string>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AvatarStorageKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    AvatarMimeType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ArchivedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_People", x => x.Id);
                    table.CheckConstraint("CK_People_AvatarMimeType_Length", "length(\"AvatarMimeType\") <= 32");
                    table.CheckConstraint("CK_People_AvatarStorageKey_Length", "length(\"AvatarStorageKey\") <= 32");
                    table.CheckConstraint("CK_People_DisplayName_Length", "length(\"DisplayName\") <= 200");
                    table.CheckConstraint("CK_People_RelationshipContext_Length", "length(\"RelationshipContext\") <= 32");
                    table.ForeignKey(
                        name: "FK_People_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserProfileExternalConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Platform = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExternalUserId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Handle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ProfileUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfileExternalConnections", x => x.Id);
                    table.CheckConstraint("CK_UserProfileExternalConnections_DisplayName_Length", "length(\"DisplayName\") <= 200");
                    table.CheckConstraint("CK_UserProfileExternalConnections_ExternalUserId_Length", "length(\"ExternalUserId\") <= 128");
                    table.CheckConstraint("CK_UserProfileExternalConnections_Handle_Length", "length(\"Handle\") <= 200");
                    table.CheckConstraint("CK_UserProfileExternalConnections_Platform_Length", "length(\"Platform\") <= 32");
                    table.CheckConstraint("CK_UserProfileExternalConnections_ProfileUrl_Length", "length(\"ProfileUrl\") <= 2048");
                    table.ForeignKey(
                        name: "FK_UserProfileExternalConnections_UserProfiles_UserProfileId",
                        column: x => x.UserProfileId,
                        principalTable: "UserProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InteractionAnalyses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InteractionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", nullable: true),
                    SchemaVersion = table.Column<string>(type: "TEXT", nullable: true),
                    PromptVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ContextFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InteractionAnalyses", x => x.Id);
                    table.CheckConstraint("CK_InteractionAnalyses_ContextFingerprint_Length", "length(\"ContextFingerprint\") <= 64");
                    table.CheckConstraint("CK_InteractionAnalyses_PromptVersion_Length", "length(\"PromptVersion\") <= 64");
                    table.CheckConstraint("CK_InteractionAnalyses_ResultJson", "\"ResultJson\" IS NULL OR json_valid(\"ResultJson\")");
                    table.ForeignKey(
                        name: "FK_InteractionAnalyses_Interactions_InteractionId",
                        column: x => x.InteractionId,
                        principalTable: "Interactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InteractionAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InteractionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    TextContent = table.Column<string>(type: "TEXT", nullable: true),
                    StorageKey = table.Column<string>(type: "TEXT", nullable: true),
                    MimeType = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InteractionAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InteractionAttachments_Interactions_InteractionId",
                        column: x => x.InteractionId,
                        principalTable: "Interactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InteractionParticipants",
                columns: table => new
                {
                    InteractionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PersonId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InteractionParticipants", x => new { x.InteractionId, x.PersonId });
                    table.ForeignKey(
                        name: "FK_InteractionParticipants_Interactions_InteractionId",
                        column: x => x.InteractionId,
                        principalTable: "Interactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InteractionParticipants_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PersonExternalConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PersonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Platform = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExternalUserId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Handle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ProfileUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonExternalConnections", x => x.Id);
                    table.CheckConstraint("CK_PersonExternalConnections_DisplayName_Length", "length(\"DisplayName\") <= 200");
                    table.CheckConstraint("CK_PersonExternalConnections_ExternalUserId_Length", "length(\"ExternalUserId\") <= 128");
                    table.CheckConstraint("CK_PersonExternalConnections_Handle_Length", "length(\"Handle\") <= 200");
                    table.CheckConstraint("CK_PersonExternalConnections_Platform_Length", "length(\"Platform\") <= 32");
                    table.CheckConstraint("CK_PersonExternalConnections_ProfileUrl_Length", "length(\"ProfileUrl\") <= 2048");
                    table.ForeignKey(
                        name: "FK_PersonExternalConnections_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PersonFacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PersonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonFacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PersonFacts_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PersonInferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PersonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    Confidence = table.Column<decimal>(type: "TEXT", nullable: false),
                    SourceInteractionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonInferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PersonInferences_Interactions_SourceInteractionId",
                        column: x => x.SourceInteractionId,
                        principalTable: "Interactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PersonInferences_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnalysisConversationMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InteractionAnalysisId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisConversationMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AnalysisConversationMessages_InteractionAnalyses_InteractionAnalysisId",
                        column: x => x.InteractionAnalysisId,
                        principalTable: "InteractionAnalyses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SuggestedProfileUpdates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PersonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InteractionAnalysisId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Field = table.Column<string>(type: "TEXT", nullable: false),
                    SuggestedValue = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AcceptedValue = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ReviewedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SuggestedProfileUpdates", x => x.Id);
                    table.CheckConstraint("CK_SuggestedProfileUpdates_Status_Length", "length(\"Status\") <= 16");
                    table.ForeignKey(
                        name: "FK_SuggestedProfileUpdates_InteractionAnalyses_InteractionAnalysisId",
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
                name: "IX_AnalysisConversationMessages_InteractionAnalysisId",
                table: "AnalysisConversationMessages",
                column: "InteractionAnalysisId");

            migrationBuilder.CreateIndex(
                name: "IX_InteractionAnalyses_InteractionId",
                table: "InteractionAnalyses",
                column: "InteractionId");

            migrationBuilder.CreateIndex(
                name: "IX_InteractionAttachments_InteractionId",
                table: "InteractionAttachments",
                column: "InteractionId");

            migrationBuilder.CreateIndex(
                name: "IX_InteractionParticipants_PersonId",
                table: "InteractionParticipants",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_Interactions_UserProfileId",
                table: "Interactions",
                column: "UserProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_People_UserProfileId",
                table: "People",
                column: "UserProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_PersonConnection_StableIdentity",
                table: "PersonExternalConnections",
                columns: new[] { "PersonId", "Platform", "ExternalUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PersonFacts_PersonId",
                table: "PersonFacts",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_PersonInferences_PersonId",
                table: "PersonInferences",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_PersonInferences_SourceInteractionId",
                table: "PersonInferences",
                column: "SourceInteractionId");

            migrationBuilder.CreateIndex(
                name: "IX_SuggestedProfileUpdates_InteractionAnalysisId",
                table: "SuggestedProfileUpdates",
                column: "InteractionAnalysisId");

            migrationBuilder.CreateIndex(
                name: "IX_SuggestedProfileUpdates_PersonId",
                table: "SuggestedProfileUpdates",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_UserConnection_StableIdentity",
                table: "UserProfileExternalConnections",
                columns: new[] { "UserProfileId", "Platform", "ExternalUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnalysisContextGuards");

            migrationBuilder.DropTable(
                name: "AnalysisConversationMessages");

            migrationBuilder.DropTable(
                name: "InteractionAttachments");

            migrationBuilder.DropTable(
                name: "InteractionParticipants");

            migrationBuilder.DropTable(
                name: "PersonExternalConnections");

            migrationBuilder.DropTable(
                name: "PersonFacts");

            migrationBuilder.DropTable(
                name: "PersonInferences");

            migrationBuilder.DropTable(
                name: "SuggestedProfileUpdates");

            migrationBuilder.DropTable(
                name: "UserProfileExternalConnections");

            migrationBuilder.DropTable(
                name: "InteractionAnalyses");

            migrationBuilder.DropTable(
                name: "People");

            migrationBuilder.DropTable(
                name: "Interactions");

            migrationBuilder.DropTable(
                name: "UserProfiles");
        }
    }
}
