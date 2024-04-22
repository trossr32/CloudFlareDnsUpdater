FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["CloudFlareDnsUpdater/CloudFlareDnsUpdater.csproj", "CloudFlareDnsUpdater/"]
RUN dotnet restore "CloudFlareDnsUpdater/CloudFlareDnsUpdater.csproj"
COPY . .
WORKDIR "/src/CloudFlareDnsUpdater"
RUN dotnet build "CloudFlareDnsUpdater.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "CloudFlareDnsUpdater.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "CloudFlareDnsUpdater.dll"]