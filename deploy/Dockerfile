# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY KasaMonitor.sln Directory.Build.props ./
COPY src/ src/
COPY tests/ tests/
COPY tools/ tools/
# Monitoring files embedded in KasaMonitor.Web.dll for the /integration page.
COPY deploy/docker-compose.monitoring.yml deploy/
COPY deploy/prometheus/ deploy/prometheus/
COPY deploy/grafana/ deploy/grafana/
RUN dotnet restore
RUN dotnet publish src/KasaMonitor.Web/KasaMonitor.Web.csproj \
    -c Release -o /app --no-self-contained -r linux-x64

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# The database is SQL Server (ConnectionStrings__Kasa, required, set by the stack); /data keeps the DataProtection keys
# (dp-keys/) and the NILM classifier.
ENV ASPNETCORE_URLS=http://0.0.0.0:8910 \
    KasaMonitor__DataDir=/data \
    TZ=Europe/Bucharest
EXPOSE 8910
VOLUME ["/data"]
# start-period: the app listens only after the database answered and the migrations ran (up to ~3 min with a slow server).
HEALTHCHECK --interval=30s --timeout=5s --retries=3 --start-period=180s \
  CMD ["dotnet", "KasaMonitor.Web.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "KasaMonitor.Web.dll"]
