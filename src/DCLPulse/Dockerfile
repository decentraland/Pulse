FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
# The image fetches its own fresh geo CSVs via ADD in the final stage; disable the
# csproj's local predownload so the build/publish steps never hit the network for them.
ENV FetchGeoDb=false
WORKDIR /build
COPY ["src/DCLPulse/DCLPulse.csproj", "src/DCLPulse/"]
COPY ["src/DCLPulse/global.json", "src/DCLPulse/"]
COPY ["src/DCLAuth/DCLAuth.csproj", "src/DCLAuth/"]
COPY ["src/Protocol/Protocol.csproj", "src/Protocol/"]
COPY ["src/Protocol/Directory.Build.props", "src/Protocol/"]
# RustEthereum auto-fetch needs the version, the MSBuild target, the local
# feed config, and the fetch script — all referenced during `dotnet restore`.
COPY ["src/Directory.Build.props", "src/"]
COPY ["src/Directory.Build.targets", "src/"]
COPY ["src/NuGet.config", "src/"]
COPY ["tools/fetch-rust-eth.sh", "tools/"]
RUN dotnet restore "src/DCLPulse/DCLPulse.csproj"
COPY src/ src/
WORKDIR "/build/src/DCLPulse"
RUN dotnet build "DCLPulse.csproj" -c $BUILD_CONFIGURATION -o /app/build -p:GenerateProto=false

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "DCLPulse.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:GenerateProto=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
ARG COMMIT_HASH=unknown
ENV COMMIT_HASH=$COMMIT_HASH
WORKDIR /app
COPY --from=publish /app/publish .
# Geo database (CC0, https://github.com/sapics/ip-location-db) — single fetch at image
# build; the app never downloads at runtime. Refreshes with every image build.
ADD https://cdn.jsdelivr.net/npm/@ip-location-db/geo-whois-asn-country/geo-whois-asn-country-ipv4-num.csv \
    https://cdn.jsdelivr.net/npm/@ip-location-db/geo-whois-asn-country/geo-whois-asn-country-ipv6-num.csv \
    /app/geodb/
EXPOSE 7777/udp
ENTRYPOINT ["dotnet", "DCLPulse.dll"]
