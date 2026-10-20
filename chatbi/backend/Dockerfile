# ChatBi.Api（.NET 8）
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ChatBi.sln global.json ./
COPY src/ChatBi.Domain/ChatBi.Domain.csproj src/ChatBi.Domain/
COPY src/ChatBi.Application/ChatBi.Application.csproj src/ChatBi.Application/
COPY src/ChatBi.Infrastructure/ChatBi.Infrastructure.csproj src/ChatBi.Infrastructure/
COPY src/ChatBi.Api/ChatBi.Api.csproj src/ChatBi.Api/
COPY tests/ChatBi.Tests/ChatBi.Tests.csproj tests/ChatBi.Tests/
RUN dotnet restore ChatBi.sln

COPY src/ src/
COPY tests/ tests/
RUN dotnet publish src/ChatBi.Api/ChatBi.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# sqlcmd 供幂等迁移脚本入口（deploy/apply-migrations.sh）在容器内执行
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl gnupg \
    && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
    && curl -fsSL https://packages.microsoft.com/config/debian/12/prod.list -o /etc/apt/sources.list.d/mssql-tools.list \
    && sed -i 's|^deb |deb [signed-by=/usr/share/keyrings/microsoft-prod.gpg] |' /etc/apt/sources.list.d/mssql-tools.list \
    && apt-get update \
    && ACCEPT_EULA=Y apt-get install -y --no-install-recommends mssql-tools18 unixodbc-dev \
    && apt-get clean && rm -rf /var/lib/apt/lists/*
USER app

EXPOSE 8080
ENTRYPOINT ["dotnet", "ChatBi.Api.dll"]
