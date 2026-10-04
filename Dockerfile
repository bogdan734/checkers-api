# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props CheckersApi.sln ./
COPY src/Checkers.Core/Checkers.Core.csproj src/Checkers.Core/
COPY src/CheckersApi/CheckersApi.csproj src/CheckersApi/
COPY src/CheckersApi.EngineCli/CheckersApi.EngineCli.csproj src/CheckersApi.EngineCli/
RUN dotnet restore src/CheckersApi/CheckersApi.csproj
COPY src/ src/
RUN dotnet publish src/CheckersApi/CheckersApi.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app .
# Linux/Docker has no Chinook binaries: run the built-in stub engine. Override with Engine__Type=chinook + Engine__Path on a box that has them.
ENV Engine__Type=stub \
    Engine__Workers=2 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
# Honour $PORT (Render, Railway, Fly, Cloud Run, Heroku-style hosts); default 8080.
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080} exec dotnet CheckersApi.dll"]
