using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BeamerPresenter.Infrastructure;

public sealed class PresenterDbContextDesignFactory : IDesignTimeDbContextFactory<PresenterDbContext>
{
    public PresenterDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PresenterDbContext>()
            .UseSqlite("Data Source=presenter.design.db")
            .Options;
        return new PresenterDbContext(options);
    }
}
