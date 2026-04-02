using Microsoft.Extensions.DependencyInjection;

namespace Kuberkynesis.LiveSurface.AspNetCore;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKuberkynesisLiveSurface(this IServiceCollection services)
    {
        return services;
    }
}
