# Gebruik .NET 8.0 runtime voor de uiteindelijke container
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

# Gebruik .NET 8.0 SDK om de app te bouwen
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Kopieer het projectbestand en herstel de dependencies
COPY ["SessyWeb/SessyWeb.csproj", "SessyWeb/"]
RUN dotnet restore "./SessyWeb/SessyWeb.csproj"

# Kopieer alle projectbestanden en bouw de applicatie
COPY . .
WORKDIR "/src/SessyWeb"
RUN dotnet build "./SessyWeb.csproj" -c $BUILD_CONFIGURATION -o /app/build /p:UseAppHost=false

# Publiceer de applicatie
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./SessyWeb.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Gebruik de .NET 9.0 runtime voor de uiteindelijke container
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "SessyWeb.dll"]
