using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReplicationDemo.DAL.Contexts;
using ReplicationDemo.DAL.Repositories;
using ReplicationDemo.Domain.Repositories;

namespace ReplicationDemo.DAL;

public static class DependencyInjection
{
    public static IServiceCollection AddDataAccess(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<PrimaryDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("Primary"),
                sqlOptions => sqlOptions.EnableRetryOnFailure()));

        services.AddDbContext<ReadOnlyDbContext>(options =>
            options.UseSqlServer(configuration.GetConnectionString("Replica"),
                sqlOptions => sqlOptions.EnableRetryOnFailure()));

        services.AddScoped<IWriteDbContext>(sp => sp.GetRequiredService<PrimaryDbContext>());
        services.AddScoped<IReadDbContext>(sp => sp.GetRequiredService<ReadOnlyDbContext>());

        services.AddScoped<IJobReadRepository, JobReadRepository>();
        services.AddScoped<IJobWriteRepository, JobWriteRepository>();

        return services;
    }
}
