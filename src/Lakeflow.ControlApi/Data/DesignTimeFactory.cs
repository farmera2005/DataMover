using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Lakeflow.ControlApi.Data;

/// <summary>Lets <c>dotnet ef migrations add</c> build the model without starting the app.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<LakeflowDb>
{
    private sealed class DesignActor : IActor
    {
        public string Name => "design";
    }

    public LakeflowDb CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<LakeflowDb>().UseSqlServer("Server=design;Database=design").Options,
        new DesignActor(), TimeProvider.System);
}
