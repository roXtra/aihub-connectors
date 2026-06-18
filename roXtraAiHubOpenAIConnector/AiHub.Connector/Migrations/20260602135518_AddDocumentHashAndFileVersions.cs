using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiHub.Connector.Migrations
{
	/// <inheritdoc />
	public partial class AddDocumentHashAndFileVersions : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<string>(name: "DocumentHash", table: "ExternalFiles", type: "TEXT", nullable: false, defaultValue: "");

			migrationBuilder.CreateTable(
				name: "ExternalFileVersions",
				columns: table => new
				{
					Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
					RoxFileId = table.Column<string>(type: "TEXT", nullable: false),
					DocumentHash = table.Column<string>(type: "TEXT", nullable: false),
					Filename = table.Column<string>(type: "TEXT", nullable: false),
					ExternalItemId = table.Column<string>(type: "TEXT", nullable: true),
					Status = table.Column<int>(type: "INTEGER", nullable: false),
					CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
					UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_ExternalFileVersions", x => x.Id);
				}
			);

			migrationBuilder.CreateIndex(name: "IX_ExternalFileVersions_ExternalItemId", table: "ExternalFileVersions", column: "ExternalItemId", unique: true);

			migrationBuilder.CreateIndex(
				name: "IX_ExternalFileVersions_RoxFileId_DocumentHash",
				table: "ExternalFileVersions",
				columns: new[] { "RoxFileId", "DocumentHash" },
				unique: true
			);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropTable(name: "ExternalFileVersions");

			migrationBuilder.DropColumn(name: "DocumentHash", table: "ExternalFiles");
		}
	}
}
