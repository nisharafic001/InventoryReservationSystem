# syntax=docker/dockerfile:1

# ---- build ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /source

# Restore first, from project files only, so the package layer is cached until a csproj changes.
COPY src/Inventory.Domain/Inventory.Domain.csproj                 src/Inventory.Domain/
COPY src/Inventory.Application/Inventory.Application.csproj       src/Inventory.Application/
COPY src/Inventory.Infrastructure/Inventory.Infrastructure.csproj src/Inventory.Infrastructure/
COPY src/Inventory.Api/Inventory.Api.csproj                       src/Inventory.Api/
RUN dotnet restore src/Inventory.Api/Inventory.Api.csproj

COPY src/ src/
RUN dotnet publish src/Inventory.Api/Inventory.Api.csproj \
        --configuration Release \
        --no-restore \
        --output /app \
        /p:UseAppHost=false

# ---- runtime --------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080

COPY --from=build /app .

# The image's built-in unprivileged user.
USER $APP_UID

# Ready = process up AND database reachable. The runtime image has no curl/wget, so use bash's /dev/tcp.
HEALTHCHECK --interval=10s --timeout=5s --start-period=30s --retries=5 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/ready HTTP/1.1\\r\\nHost: localhost\\r\\nConnection: close\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]

ENTRYPOINT ["dotnet", "Inventory.Api.dll"]
