# Runtime image for the final container.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

ENV DOTNET_RUNNING_IN_DOCKER=true

# Build on the target platform, not cross-compiled. Cross-compiling with
# --platform=$BUILDPLATFORM plus -a $TARGETARCH shipped an image whose
# wwwroot/_framework was missing: the package-provided assets under _content came
# through, but blazor.server.js, which comes from the architecture-specific runtime
# pack, did not. The publish still succeeded and the app still started, so the only
# symptom was every page rendering its prerendered shell with no data.
# Building for arm64 emulates the SDK under QEMU and is slower; correctness wins.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Project files first, so the restore layer stays cached until a package or
# project reference actually changes.
COPY ["SessyWeb/SessyWeb.csproj", "SessyWeb/"]
COPY ["SessyController/SessyController.csproj", "SessyController/"]
COPY ["SessyData/SessyData.csproj", "SessyData/"]
COPY ["SessyCommon/SessyCommon.csproj", "SessyCommon/"]
COPY ["Djohnnie.SolarEdge.ModBus.TCP/Djohnnie.SolarEdge.ModBus.TCP.csproj", "Djohnnie.SolarEdge.ModBus.TCP/"]
RUN dotnet restore "SessyWeb/SessyWeb.csproj"

# Publish builds as well, so there is no separate build step.
#
# Deliberately NOT --no-restore. In .NET 10 blazor.server.js ships in the NuGet
# package Microsoft.AspNetCore.App.Internal.Assets, which the SDK adds as an
# implicit reference during the build — the restore above never sees it. With
# --no-restore the package is never fetched and wwwroot/_framework silently
# disappears from the publish output: the app builds, starts and serves pages,
# but the browser 404s on the script and no Blazor circuit is ever created.
# The restore layer above still pays off; this second restore finds the rest
# up to date.
COPY . .
RUN dotnet publish "SessyWeb/SessyWeb.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish \
    /p:UseAppHost=false

# A Blazor Server image without this script starts normally and serves pages that
# never connect: the browser 404s on it, no circuit is created, and every page
# renders its prerendered shell with no data. Fail the build instead of shipping that.
RUN test -s /app/publish/wwwroot/_framework/blazor.server.js \
    || (echo "blazor.server.js missing from the publish output" && exit 1)

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

# Same check against what actually ends up in the image, so a bad COPY or a stale
# layer cannot slip through either.
RUN test -s /app/wwwroot/_framework/blazor.server.js \
    || (echo "blazor.server.js missing from the image" && exit 1)

ENTRYPOINT ["dotnet", "SessyWeb.dll"]
