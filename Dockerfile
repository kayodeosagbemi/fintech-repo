# syntax=docker/dockerfile:1
# Multi-stage build for Plooto.Ap.Api. The API host references every service project (PLAN_1 section 4), so all of their
# csproj files are copied first to keep `dotnet restore` cached independently of source changes.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/Plooto.Ap.Domain/Plooto.Ap.Domain.csproj src/Plooto.Ap.Domain/
COPY src/Plooto.Ap.Infrastructure/Plooto.Ap.Infrastructure.csproj src/Plooto.Ap.Infrastructure/
COPY src/Plooto.Ap.Api.Contracts/Plooto.Ap.Api.Contracts.csproj src/Plooto.Ap.Api.Contracts/
COPY src/Plooto.Ap.Messaging/Plooto.Ap.Messaging.csproj src/Plooto.Ap.Messaging/
COPY src/Plooto.Ap.Services.Validation/Plooto.Ap.Services.Validation.csproj src/Plooto.Ap.Services.Validation/
COPY src/Plooto.Ap.Services.Payment/Plooto.Ap.Services.Payment.csproj src/Plooto.Ap.Services.Payment/
COPY src/Plooto.Ap.Services.Notification/Plooto.Ap.Services.Notification.csproj src/Plooto.Ap.Services.Notification/
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
# survives container restarts (see PLAN_1.md section 1.4 for the data/ convention).
RUN mkdir -p /app/data

# Development enables the seed data (two demo companies with fixed, obviously-fake API keys). Set to Production to disable.
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Development
EXPOSE 8080

ENTRYPOINT ["dotnet", "Plooto.Ap.Api.dll"]
