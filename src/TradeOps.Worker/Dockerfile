FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["src/TradeOps.Domain/TradeOps.Domain.csproj", "src/TradeOps.Domain/"]
COPY ["src/TradeOps.Application/TradeOps.Application.csproj", "src/TradeOps.Application/"]
COPY ["src/TradeOps.Infrastructure/TradeOps.Infrastructure.csproj", "src/TradeOps.Infrastructure/"]
COPY ["src/TradeOps.Worker/TradeOps.Worker.csproj", "src/TradeOps.Worker/"]
RUN dotnet restore "src/TradeOps.Worker/TradeOps.Worker.csproj"

COPY . .
RUN dotnet publish "src/TradeOps.Worker/TradeOps.Worker.csproj" \
    --configuration Release \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TradeOps.Worker.dll"]
