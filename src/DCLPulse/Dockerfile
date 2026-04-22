FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /build
COPY ["src/DCLPulse/DCLPulse.csproj", "src/DCLPulse/"]
COPY ["src/DCLPulse/global.json", "src/DCLPulse/"]
COPY ["src/DCLAuth/DCLAuth.csproj", "src/DCLAuth/"]
COPY ["src/Protocol/Protocol.csproj", "src/Protocol/"]
COPY ["src/Protocol/Directory.Build.props", "src/Protocol/"]
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
EXPOSE 7777/udp
ENTRYPOINT ["dotnet", "DCLPulse.dll"]
