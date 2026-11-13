FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/MarketFlow.Api/MarketFlow.Api.csproj src/MarketFlow.Api/
RUN dotnet restore src/MarketFlow.Api/MarketFlow.Api.csproj
COPY src/MarketFlow.Api/ src/MarketFlow.Api/
RUN dotnet publish src/MarketFlow.Api/MarketFlow.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "MarketFlow.Api.dll"]
