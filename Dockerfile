# The MBR image for Azure Container Apps: API, web console and job worker in one process.
# Built in Azure Container Registry by the Terraform deployment (deploy/terraform, `az acr build`).

FROM node:24-alpine AS web
WORKDIR /src/src/web
COPY src/web/package*.json ./
RUN npm ci
COPY src/web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
COPY --from=web /src/src/Mbr.Api/wwwroot ./src/Mbr.Api/wwwroot
RUN dotnet publish src/Mbr.Api -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# PowerShell 7 + modules for Exchange Online, Teams and Security & Compliance configuration backups.
RUN apt-get update && apt-get install -y --no-install-recommends wget ca-certificates \
 && wget -q https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb \
 && dpkg -i packages-microsoft-prod.deb && rm packages-microsoft-prod.deb \
 && apt-get update && apt-get install -y --no-install-recommends powershell \
 && pwsh -NoProfile -Command "Set-PSRepository PSGallery -InstallationPolicy Trusted; Install-Module ExchangeOnlineManagement, MicrosoftTeams -Scope AllUsers -Force" \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    Mbr__DataDirectory=/data
# Scratch space for the non-root app user (state lives in PostgreSQL, Key Vault and S3).
RUN mkdir -p /data && chown $APP_UID:$APP_UID /data
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Mbr.Api.dll"]
