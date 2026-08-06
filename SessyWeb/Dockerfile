# Runtime image for the final container.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

ENV DOTNET_RUNNING_IN_DOCKER=true

# Build runs on the runner's own architecture and cross-compiles to the target,
# which is far faster than emulating the SDK under QEMU for arm64.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
WORKDIR /src

# Project files first, so the restore layer stays cached until a package or
# project reference actually changes.
COPY ["SessyWeb/SessyWeb.csproj", "SessyWeb/"]
COPY ["SessyController/SessyController.csproj", "SessyController/"]
COPY ["SessyData/SessyData.csproj", "SessyData/"]
COPY ["SessyCommon/SessyCommon.csproj", "SessyCommon/"]
COPY ["Djohnnie.SolarEdge.ModBus.TCP/Djohnnie.SolarEdge.ModBus.TCP.csproj", "Djohnnie.SolarEdge.ModBus.TCP/"]
RUN dotnet restore "SessyWeb/SessyWeb.csproj" -a $TARGETARCH

# Publish builds as well, so there is no separate build step.
COPY . .
RUN dotnet publish "SessyWeb/SessyWeb.csproj" \
    -c $BUILD_CONFIGURATION \
    -a $TARGETARCH \
    --no-restore \
    -o /app/publish \
    /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "SessyWeb.dll"]
