# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# curl is only needed for the HEALTHCHECK below - the aspnet runtime image doesn't ship it.
USER root
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
USER $APP_UID


# This stage builds the React dashboard (spreadtrader.dashboard.client) so its static output can
# be dropped into the API's wwwroot below - Program.cs serves it from the same origin/process via
# UseStaticFiles + MapFallbackToFile, so the client's relative /api and /hubs calls just work with
# no CORS needed. This is what makes "one container, one URL" possible instead of hosting the API
# and the dashboard as two separate deployments.
FROM node:20-alpine AS client-build
WORKDIR /client
COPY ["SpreadTrader.Dashboard/spreadtrader.dashboard.client/package.json", "SpreadTrader.Dashboard/spreadtrader.dashboard.client/package-lock.json*", "./"]
RUN npm ci
COPY ["SpreadTrader.Dashboard/spreadtrader.dashboard.client/", "./"]
RUN npm run build

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["LiveSpreadCalculator/LiveSpreadCalculator.csproj", "LiveSpreadCalculator/"]
COPY ["SpreadTrader.Core/SpreadTrader.Core.csproj", "SpreadTrader.Core/"]
COPY ["SpreadTrader.KiteIntegration/SpreadTrader.KiteIntegration.csproj", "SpreadTrader.KiteIntegration/"]
COPY ["SpreadTrader.Engine/SpreadTrader.Engine.csproj", "SpreadTrader.Engine/"]
RUN dotnet restore "./LiveSpreadCalculator/LiveSpreadCalculator.csproj"
COPY . .
WORKDIR "/src/LiveSpreadCalculator"
RUN dotnet build "./LiveSpreadCalculator.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./LiveSpreadCalculator.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# Vite's default build output directory is "dist" - becomes wwwroot so UseStaticFiles/
# MapFallbackToFile in Program.cs pick it up automatically (ASP.NET Core's default web root).
COPY --from=client-build /client/dist ./wwwroot

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080

# Everything under appsettings.json's "Kite" and "Dashboard" sections can be overridden with
# double-underscore env vars at `docker run` time (ASP.NET Core's standard config convention),
# e.g.:
#   -e Kite__ApiKey=your_api_key
#   -e Kite__ApiSecret=your_api_secret
#   -e Kite__AccessToken=today_s_access_token   (empty until you complete the login flow)
#   -e Dashboard__BaseUrl=https://your-dashboard-domain
# Never bake ApiKey/ApiSecret into the image itself - pass them at runtime (env vars, a
# secrets manager, or your platform's equivalent), not via appsettings.json in source control.

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "LiveSpreadCalculator.dll"]