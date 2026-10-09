FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["src/TradeOps.Domain/TradeOps.Domain.csproj", "src/TradeOps.Domain/"]
COPY ["src/TradeOps.Application/TradeOps.Application.csproj", "src/TradeOps.Application/"]
COPY ["src/TradeOps.Infrastructure/TradeOps.Infrastructure.csproj", "src/TradeOps.Infrastructure/"]
COPY ["src/TradeOps.Api/TradeOps.Api.csproj", "src/TradeOps.Api/"]
RUN dotnet restore "src/TradeOps.Api/TradeOps.Api.csproj"

COPY . .
RUN dotnet publish "src/TradeOps.Api/TradeOps.Api.csproj" \
    --configuration Release \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TradeOps.Api.dll"]
