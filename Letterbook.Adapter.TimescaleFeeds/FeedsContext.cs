using System.Reflection;
using Letterbook.Adapter.TimescaleFeeds.EntityModels;
using Letterbook.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Letterbook.Adapter.TimescaleFeeds;

public class FeedsContext : DbContext
{
	public DbSet<TimelinePost> Timelines { get; set; } = null!;

	// Called by the designer to create and run migrations
	public FeedsContext(DbContextOptions<FeedsContext> options) : base(options)
	{
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		base.OnModelCreating(modelBuilder);
		modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
	}
}