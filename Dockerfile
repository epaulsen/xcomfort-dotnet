FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/XComfortNet/XComfortNet.csproj src/XComfortNet/
COPY src/XComfortNet.Web/XComfortNet.Web.csproj src/XComfortNet.Web/
RUN dotnet restore src/XComfortNet.Web/XComfortNet.Web.csproj

COPY src/XComfortNet/ src/XComfortNet/
COPY src/XComfortNet.Web/ src/XComfortNet.Web/
RUN dotnet publish src/XComfortNet.Web/XComfortNet.Web.csproj \
    --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish/ ./
RUN mkdir -p /app/data && chown "$APP_UID" /app/data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "XComfortNet.Web.dll"]
