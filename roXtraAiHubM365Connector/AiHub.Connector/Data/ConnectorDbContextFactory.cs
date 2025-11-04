using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AiHub.Connector.Data;

public class ConnectorDbContextFactory : IDesignTimeDbContextFactory<ConnectorDbContext>
{
	public ConnectorDbContext CreateDbContext(string[] args)
	{
		var optionsBuilder = new DbContextOptionsBuilder<ConnectorDbContext>();

		// Use the same default as Program.cs if no configuration is available
		var connectionString = "Data Source=connector.db";
		_ = optionsBuilder.UseSqlite(connectionString);

		return new ConnectorDbContext(optionsBuilder.Options);
	}
}
