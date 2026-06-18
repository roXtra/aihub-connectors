using AiHub.Connector.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AiHub.Connector.Data;

public class ConnectorDbContext : DbContext
{
	public ConnectorDbContext(DbContextOptions<ConnectorDbContext> options)
		: base(options) { }

	public DbSet<ExternalFileEntity> ExternalFiles => Set<ExternalFileEntity>();
	public DbSet<ExternalFileVersionEntity> ExternalFileVersions => Set<ExternalFileVersionEntity>();
	public DbSet<ExternalGroupEntity> ExternalGroups => Set<ExternalGroupEntity>();
	public DbSet<FileKnowledgePoolEntity> FileKnowledgePools => Set<FileKnowledgePoolEntity>();

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);

		_ = modelBuilder.Entity<ExternalFileEntity>(b =>
		{
			_ = b.HasKey(x => x.Id);
			_ = b.Property(x => x.RoxFileId).IsRequired();
			_ = b.Property(x => x.ExternalItemId).IsRequired();
			_ = b.Property(x => x.DocumentHash).IsRequired();
			_ = b.HasIndex(x => x.RoxFileId).IsUnique();
			_ = b.HasIndex(x => x.ExternalItemId).IsUnique();
		});

		_ = modelBuilder.Entity<ExternalFileVersionEntity>(b =>
		{
			_ = b.HasKey(x => x.Id);
			_ = b.Property(x => x.RoxFileId).IsRequired();
			_ = b.Property(x => x.DocumentHash).IsRequired();
			_ = b.Property(x => x.Filename).IsRequired();
			_ = b.Property(x => x.Status).IsRequired();
			_ = b.HasIndex(x => new { x.RoxFileId, x.DocumentHash }).IsUnique();
			_ = b.HasIndex(x => x.ExternalItemId).IsUnique();
			// Filename unique index intentionally omitted: multiple files can share a sanitized title.
			// The (RoxFileId, DocumentHash) index is the authoritative uniqueness constraint.
		});

		_ = modelBuilder.Entity<ExternalGroupEntity>(b =>
		{
			_ = b.HasKey(x => x.Id);
			_ = b.Property(x => x.KnowledgePoolId).IsRequired();
			_ = b.Property(x => x.ExternalGroupId).IsRequired();
			_ = b.HasIndex(x => x.KnowledgePoolId).IsUnique();
			_ = b.HasIndex(x => x.ExternalGroupId).IsUnique();
		});

		_ = modelBuilder.Entity<FileKnowledgePoolEntity>(b =>
		{
			_ = b.HasKey(x => x.Id);
			_ = b.Property(x => x.RoxFileId).IsRequired();
			_ = b.Property(x => x.KnowledgePoolId).IsRequired();
			_ = b.HasIndex(x => new { x.RoxFileId, x.KnowledgePoolId }).IsUnique();
		});
	}
}
