FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY packages/ packages/
COPY apps/world-server/ apps/world-server/
RUN dotnet publish apps/world-server -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 9050/udp
ENTRYPOINT ["dotnet", "WorldServer.dll"]
