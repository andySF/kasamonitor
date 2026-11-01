# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY KasaMonitor.sln Directory.Build.props ./
COPY src/ src/
COPY tests/ tests/
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
ENV ASPNETCORE_URLS=http://0.0.0.0:8910 \
    KasaMonitor__DbPath=/data/kasa.db \
    TZ=Europe/Bucharest
EXPOSE 8910
VOLUME ["/data"]
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
  CMD ["dotnet", "KasaMonitor.Web.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "KasaMonitor.Web.dll"]
