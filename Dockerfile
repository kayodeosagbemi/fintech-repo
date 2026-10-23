# syntax=docker/dockerfile:1
# Multi-stage build for Plooto.Ap.Api — see PLAN_1.md §1.2 for the project layout this assumes.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj files first so `dotnet restore` is cached independently of source changes.
COPY src/Plooto.Ap.Domain/Plooto.Ap.Domain.csproj src/Plooto.Ap.Domain/
COPY src/Plooto.Ap.Infrastructure/Plooto.Ap.Infrastructure.csproj src/Plooto.Ap.Infrastructure/
COPY src/Plooto.Ap.Api/Plooto.Ap.Api.csproj src/Plooto.Ap.Api/
RUN dotnet restore src/Plooto.Ap.Api/Plooto.Ap.Api.csproj

COPY src/ src/
RUN dotnet publish src/Plooto.Ap.Api/Plooto.Ap.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# data/ holds the SQLite file at runtime; bind-mounted by docker-compose.yml so it
# survives container restarts (see PLAN_1.md §1.4 for the data/ convention).
RUN mkdir -p /app/data

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Development
EXPOSE 8080

ENTRYPOINT ["dotnet", "Plooto.Ap.Api.dll"]
