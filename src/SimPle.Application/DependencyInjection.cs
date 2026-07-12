using Microsoft.Extensions.DependencyInjection;
using SimPle.Application.Auth.Services;
using SimPle.Application.Expiry;
using SimPle.Application.Friends.Services;
using SimPle.Application.GameHost.Services;
using SimPle.Application.Games.Services;
using SimPle.Application.Lobbies.Services;
using SimPle.Application.Matchmaking.Services;
using SimPle.Application.Outbox;
using SimPle.Application.Outbox.Handlers;
using SimPle.Application.People.Services;
using SimPle.Application.Profiles.Services;

namespace SimPle.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IProfileService, ProfileService>();
        services.AddScoped<IFriendsService, FriendsService>();
        services.AddScoped<IPeopleService, PeopleService>();
        services.AddScoped<IGamesService, GamesService>();
        services.AddScoped<ILobbiesService, LobbiesService>();

        // Module 6, slice 6C. The coordinator and the sweeper are application services rather than logic inside a
        // BackgroundService on purpose: that is what lets the real-Postgres tests run two coordinators concurrently
        // and assert zero duplicate assignment, which a timer tick buried in a hosted service could not.
        services.AddScoped<IMatchmakingService, MatchmakingService>();
        services.AddScoped<IMatchmakingCoordinator, MatchmakingCoordinator>();
        services.AddScoped<IExpirySweeper, ExpirySweeper>();

        // The outbox dispatcher (D3) — the codebase's first consumer side. M7/M8/M11 register their own
        // IOutboxHandler here and inherit the machinery; they do not each build a consumer.
        services.AddScoped<IOutboxProcessor, OutboxProcessor>();
        services.AddScoped<IOutboxHandler, LobbyBlockHandler>();

        // IGameRegistry is registered separately by the composition root: building it requires the list of
        // installed IHostedGameDefinition instances, which is composition-root knowledge (currently empty —
        // no Phase 2 game is hosted yet), not something this generic module wiring can supply.
        services.AddScoped<IGameHostInvoker, GameHostInvoker>();
        services.AddScoped<ICatalogEngineCompatibilityValidator, CatalogEngineCompatibilityValidator>();

        return services;
    }
}
