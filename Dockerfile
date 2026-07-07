# Build em múltiplos estágios: o SDK (pesado) só existe durante a compilação;
# a imagem final leva apenas o runtime do ASP.NET + os binários publicados.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copia só os arquivos de projeto primeiro: o restore fica em cache
# enquanto os csproj não mudarem, acelerando rebuilds.
COPY PaymentGateway.slnx Directory.Build.props ./
COPY src/PaymentGateway.Domain/PaymentGateway.Domain.csproj src/PaymentGateway.Domain/
COPY src/PaymentGateway.Data/PaymentGateway.Data.csproj src/PaymentGateway.Data/
COPY src/PaymentGateway.Services/PaymentGateway.Services.csproj src/PaymentGateway.Services/
COPY src/PaymentGateway.Api/PaymentGateway.Api.csproj src/PaymentGateway.Api/
RUN dotnet restore src/PaymentGateway.Api/PaymentGateway.Api.csproj

COPY src/ src/
RUN dotnet publish src/PaymentGateway.Api/PaymentGateway.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "PaymentGateway.Api.dll"]
