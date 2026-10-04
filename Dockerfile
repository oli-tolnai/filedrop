FROM node:24-alpine AS web-build
WORKDIR /src/web
RUN corepack enable
COPY src/FileDrop.Web/package.json src/FileDrop.Web/pnpm-lock.yaml src/FileDrop.Web/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
COPY src/FileDrop.Web/ ./
RUN pnpm build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src
COPY src/FileDrop.Api/FileDrop.Api.csproj src/FileDrop.Api/
RUN dotnet restore src/FileDrop.Api/FileDrop.Api.csproj
COPY src/FileDrop.Api/ src/FileDrop.Api/
RUN dotnet publish src/FileDrop.Api/FileDrop.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false
COPY --from=web-build /src/web/dist/filedrop-web/browser/ /app/publish/wwwroot/

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
COPY --from=api-build --chown=$APP_UID:$APP_UID /app/publish ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "FileDrop.Api.dll"]
