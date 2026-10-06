using Inventory.Application.Auth;
using Inventory.Application.Products;
using Inventory.Application.Reservations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Inventory.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IReservationService, ReservationService>();
        services.AddScoped<IReservationExpiryService, ReservationExpiryService>();

        return services;
    }
}
