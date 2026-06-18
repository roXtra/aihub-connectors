using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiHub.Connector.Migrations
{
	/// <inheritdoc />
	public partial class InitialCreate : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			_ = migrationBuilder.CreateTable(
				name: "ExternalFiles",
				columns: table => new
				{
					Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
					RoxFileId = table.Column<string>(type: "TEXT", nullable: false),
					ExternalItemId = table.Column<string>(type: "TEXT", nullable: false),
					CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
				},
				constraints: table =>
				{
					_ = table.PrimaryKey("PK_ExternalFiles", x => x.Id);
				}
			);

			_ = migrationBuilder.CreateTable(
				name: "ExternalGroups",
				columns: table => new
				{
					Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
					KnowledgePoolId = table.Column<string>(type: "TEXT", nullable: false),
					ExternalGroupId = table.Column<string>(type: "TEXT", nullable: false),
					CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
				},
				constraints: table =>
				{
					_ = table.PrimaryKey("PK_ExternalGroups", x => x.Id);
				}
			);

			_ = migrationBuilder.CreateTable(
				name: "FileKnowledgePools",
				columns: table => new
				{
					Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
					RoxFileId = table.Column<string>(type: "TEXT", nullable: false),
					KnowledgePoolId = table.Column<string>(type: "TEXT", nullable: false),
					CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
				},
				constraints: table =>
				{
					_ = table.PrimaryKey("PK_FileKnowledgePools", x => x.Id);
				}
			);

			_ = migrationBuilder.CreateIndex(name: "IX_ExternalFiles_ExternalItemId", table: "ExternalFiles", column: "ExternalItemId", unique: true);

			_ = migrationBuilder.CreateIndex(name: "IX_ExternalFiles_RoxFileId", table: "ExternalFiles", column: "RoxFileId", unique: true);

			_ = migrationBuilder.CreateIndex(name: "IX_ExternalGroups_ExternalGroupId", table: "ExternalGroups", column: "ExternalGroupId", unique: true);

			_ = migrationBuilder.CreateIndex(name: "IX_ExternalGroups_KnowledgePoolId", table: "ExternalGroups", column: "KnowledgePoolId", unique: true);

			_ = migrationBuilder.CreateIndex(
				name: "IX_FileKnowledgePools_RoxFileId_KnowledgePoolId",
				table: "FileKnowledgePools",
				columns: new[] { "RoxFileId", "KnowledgePoolId" },
				unique: true
			);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			_ = migrationBuilder.DropTable(name: "FileKnowledgePools");

			_ = migrationBuilder.DropTable(name: "ExternalFiles");

			_ = migrationBuilder.DropTable(name: "ExternalGroups");
		}
	}
}
