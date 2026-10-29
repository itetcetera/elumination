# DRONE in one image: the game server, its settings, and the browser client on one port (8080).
#   docker build -t drone .
#   docker run -p 8080:8080 drone

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS server
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Drone.Engine/Drone.Engine.csproj src/Drone.Engine/
COPY src/Drone.Server/Drone.Server.csproj src/Drone.Server/
RUN dotnet restore src/Drone.Server/Drone.Server.csproj
COPY src/Drone.Engine/ src/Drone.Engine/
COPY src/Drone.Server/ src/Drone.Server/
RUN dotnet publish src/Drone.Server/Drone.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=server /app ./
COPY settings/ ./settings/
COPY client/ ./client/
ENV DRONE_SETTINGS_DIR=/app/settings \
    ClientPath=/app/client \
    ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Drone.Server.dll"]
