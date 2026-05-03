# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution + build props first for better layer caching.
COPY global.json Directory.Build.props Directory.Packages.props McpDatabaseQueryApp.slnx ./
COPY src ./src

# Restore + publish the server. UI bundles are committed to ui-dist/, so we
# skip the npm-driven UI build to avoid pulling Node into the build image.
RUN dotnet restore src/McpDatabaseQueryApp.Server/McpDatabaseQueryApp.Server.csproj
RUN dotnet publish src/McpDatabaseQueryApp.Server/McpDatabaseQueryApp.Server.csproj \
      -c Release \
      -o /app/publish \
      -p:SkipUiBuild=true \
      -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

RUN mkdir -p /data && chown -R 1000:1000 /data
USER 1000:1000

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_ENVIRONMENT=Production

EXPOSE 8080
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "mcp-database-query-app.dll"]
