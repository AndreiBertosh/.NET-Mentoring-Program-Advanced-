# Build context: repo root
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/ReplicationDemo.Worker/ReplicationDemo.Worker.csproj",         "src/ReplicationDemo.Worker/"]
COPY ["src/ReplicationDemo.Application/ReplicationDemo.Application.csproj", "src/ReplicationDemo.Application/"]
COPY ["src/ReplicationDemo.DAL/ReplicationDemo.DAL.csproj",               "src/ReplicationDemo.DAL/"]
COPY ["src/ReplicationDemo.Domain/ReplicationDemo.Domain.csproj",         "src/ReplicationDemo.Domain/"]
COPY ["src/ReplicationDemo.Messaging/ReplicationDemo.Messaging.csproj",   "src/ReplicationDemo.Messaging/"]
RUN dotnet restore "src/ReplicationDemo.Worker/ReplicationDemo.Worker.csproj"
COPY . .
WORKDIR "/src/src/ReplicationDemo.Worker"
RUN dotnet publish "ReplicationDemo.Worker.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ReplicationDemo.Worker.dll"]
