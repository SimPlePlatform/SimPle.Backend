# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["src/SimPle.Api/SimPle.Api.csproj", "src/SimPle.Api/"]
COPY ["src/SimPle.Application/SimPle.Application.csproj", "src/SimPle.Application/"]
COPY ["src/SimPle.Domain/SimPle.Domain.csproj", "src/SimPle.Domain/"]
COPY ["src/SimPle.Infrastructure/SimPle.Infrastructure.csproj", "src/SimPle.Infrastructure/"]
COPY ["src/SimPle.Shared/SimPle.Shared.csproj", "src/SimPle.Shared/"]
RUN dotnet restore "src/SimPle.Api/SimPle.Api.csproj"

COPY src/ ./src/
RUN dotnet publish "src/SimPle.Api/SimPle.Api.csproj" --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Container Apps probes this port. Secrets are injected by the platform as environment variables, never copied here.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080

COPY --from=build /app/publish ./

# The official ASP.NET image supplies this non-root user (UID 1654). The application does not require a writable
# filesystem, allowing the deployment manifest to set a read-only root filesystem as an additional hardening layer.
USER $APP_UID
ENTRYPOINT ["dotnet", "SimPle.Api.dll"]
