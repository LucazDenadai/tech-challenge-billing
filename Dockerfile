FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/OficinaMecanica.Billing.Domain/OficinaMecanica.Billing.Domain.csproj src/OficinaMecanica.Billing.Domain/
COPY src/OficinaMecanica.Billing.Application/OficinaMecanica.Billing.Application.csproj src/OficinaMecanica.Billing.Application/
COPY src/OficinaMecanica.Billing.Infrastructure/OficinaMecanica.Billing.Infrastructure.csproj src/OficinaMecanica.Billing.Infrastructure/
COPY src/OficinaMecanica.Billing.API/OficinaMecanica.Billing.API.csproj src/OficinaMecanica.Billing.API/

RUN dotnet restore src/OficinaMecanica.Billing.API/OficinaMecanica.Billing.API.csproj

COPY src/ src/

RUN dotnet publish src/OficinaMecanica.Billing.API/OficinaMecanica.Billing.API.csproj \
    -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# Versão da imagem para a telemetria (service.version); o CI passa sha-<commit>.
ARG VERSAO=local
ENV Servico__Versao=$VERSAO
COPY --from=build --chown=app:app --chmod=755 /app/publish .
USER app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "OficinaMecanica.Billing.API.dll"]
