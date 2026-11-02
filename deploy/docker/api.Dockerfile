# syntax=docker/dockerfile:1.10
# iPop API (ASP.NET, .NET 10) for the k3s cluster.
#
# DevExpress packages are not on nuget.org. Two ways in, neither ends up in an image layer:
#   CI:    --secret id=dx_feed (https://nuget.devexpress.com/<key>/api/v3/index.json)
#   local: --build-context dxpkgs=<folder with the DevExpress *.nupkg files>
# The license goes in the same way: --secret id=dx_license (contents of DevExpress_License.txt).

FROM scratch AS dxpkgs

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/IPop.Core/IPop.Core.csproj src/IPop.Core/
COPY src/IPop.Application/IPop.Application.csproj src/IPop.Application/
COPY src/IPop.Infrastructure/IPop.Infrastructure.csproj src/IPop.Infrastructure/
COPY src/IPop.Api/IPop.Api.csproj src/IPop.Api/
RUN --mount=type=bind,from=dxpkgs,target=/dxpkgs \
    --mount=type=secret,id=dx_feed,env=DX_FEED \
    --mount=type=cache,target=/root/.nuget/packages \
    set -e; \
    if [ -n "$(ls -A /dxpkgs 2>/dev/null)" ]; then dotnet nuget add source /dxpkgs -n dx; \
    elif [ -n "$DX_FEED" ]; then dotnet nuget add source "$DX_FEED" -n dx; \
    else echo "No DevExpress packages: pass --secret id=dx_feed or --build-context dxpkgs=..." >&2; exit 1; fi; \
    dotnet restore src/IPop.Api/IPop.Api.csproj; \
    dotnet nuget remove source dx
COPY src/ src/
RUN --mount=type=secret,id=dx_license,env=DevExpress_License \
    --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/IPop.Api/IPop.Api.csproj -c Release --no-restore -o /out /p:UseAppHost=false \
 && find /out/runtimes -mindepth 1 -maxdepth 1 ! -name linux-x64 -exec rm -rf {} + \
 && find /out -name '*.pdb' -delete

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Fonts + fontconfig for DevExpress (SkiaSharp) PDF/Office rendering; tzdata for local-time output.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libfontconfig1 libfreetype6 libgssapi-krb5-2 fonts-dejavu-core fonts-liberation tzdata \
 && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    TZ=Asia/Jakarta \
    DOTNET_gcServer=0
WORKDIR /app
COPY --from=build /out ./
COPY --chmod=755 deploy/docker/api-entrypoint.sh /usr/local/bin/ipop-api
# Upload/cache folders under the content root live on a persistent volume mounted at /data.
RUN mkdir -p /data && chown $APP_UID /data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/ipop-api"]
