# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json ./
COPY src/SamosControl.Domain/*.csproj src/SamosControl.Domain/
COPY src/SamosControl.Infrastructure/*.csproj src/SamosControl.Infrastructure/
COPY src/SamosControl.Integrations/*.csproj src/SamosControl.Integrations/
COPY src/SamosControl.Application/*.csproj src/SamosControl.Application/
COPY src/SamosControl.Web/*.csproj src/SamosControl.Web/
COPY src/SamosControl.Worker/*.csproj src/SamosControl.Worker/
RUN dotnet restore src/SamosControl.Web/SamosControl.Web.csproj
COPY src/ src/
RUN dotnet publish src/SamosControl.Web/SamosControl.Web.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
# libfontconfig1 + fonts: QuestPDF (contract PDFs). tzdata: America/Sao_Paulo. curl: healthcheck.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core tzdata curl \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --system --uid 10001 --home-dir /app --shell /usr/sbin/nologin samos \
    && mkdir -p /data && chown samos:samos /data
WORKDIR /app
COPY --from=build /app/publish .
COPY docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN sed -i 's/\r$//' /usr/local/bin/docker-entrypoint.sh && chmod +x /usr/local/bin/docker-entrypoint.sh

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    STORAGE_PATH=/data \
    TIMEZONE=America/Sao_Paulo \
    DOTNET_gcServer=0
VOLUME ["/data"]
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh"]
CMD ["dotnet", "SamosControl.Web.dll"]
