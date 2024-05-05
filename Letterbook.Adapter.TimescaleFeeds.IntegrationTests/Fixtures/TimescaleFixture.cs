using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Letterbook.Adapter.TimescaleFeeds.IntegrationTests.Fixtures;

public class TimescaleFixture
{
	private const string ConnectionString =
		"Server=localhost;" +
		"Port=5433;" +
		"Database=letterbook_feeds_tests;" +
		"User Id=letterbook;" +
		"Password=letterbookpw;" +
		"SSL Mode=Disable;" +
		"Search Path=public;" +
		"Include Error Detail=true";
	private static readonly object _lock = new();
	private static bool _databaseInitialized;

	private DbContextOptions<FeedsContext> _opts = new DbContextOptionsBuilder<FeedsContext>()
		.UseNpgsql(DependencyInjection.DataSource(new FeedsDbOptions() { ConnectionString = ConnectionString }))
		.Options;

	public FeedsContext CreateContext() => new(_opts);

	public TimescaleFixture()
	{
		lock (_lock)
		{
			if (_databaseInitialized) return;
			using (FeedsContext context = CreateContext())
			{
				context.Database.EnsureDeleted();
				context.Database.Migrate();

				// populate the db with test data
				// context.AddRange();
				context.SaveChanges();
			}

			_databaseInitialized = true;
		}
	}

}