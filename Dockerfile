FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY netbook-service.csproj .
RUN dotnet restore netbook-service.csproj
COPY . .
RUN dotnet publish netbook-service.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
# Pending EF Core migrations are applied at startup (Program.cs), so no
# separate migrate step is needed here.
EXPOSE 8080
ENTRYPOINT ["dotnet", "netbook-service.dll"]
