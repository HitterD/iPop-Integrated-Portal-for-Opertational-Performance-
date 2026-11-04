# syntax=docker/dockerfile:1.10
# iPop Web: IPop.WebHost serving the Blazor WebAssembly client.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/IPop.Core/IPop.Core.csproj src/IPop.Core/
COPY src/IPop.Application/IPop.Application.csproj src/IPop.Application/
COPY src/IPop.Web/IPop.Web.csproj src/IPop.Web/
COPY src/IPop.WebHost/IPop.WebHost.csproj src/IPop.WebHost/
RUN dotnet restore src/IPop.WebHost/IPop.WebHost.csproj
COPY src/IPop.Core/ src/IPop.Core/
COPY src/IPop.Application/ src/IPop.Application/
COPY src/IPop.Web/ src/IPop.Web/
COPY src/IPop.WebHost/ src/IPop.WebHost/
RUN dotnet publish src/IPop.WebHost/IPop.WebHost.csproj -c Release --no-restore -o /out /p:UseAppHost=false \
 && find /out -name '*.pdb' -delete

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080
WORKDIR /app
COPY --from=build /out ./
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "IPop.WebHost.dll"]
