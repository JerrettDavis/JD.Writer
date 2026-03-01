# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["JD.Writer.sln", "./"]
COPY ["Directory.Build.props", "./"]
COPY ["global.json", "./"]
COPY ["version.json", "./"]
COPY ["JD.Writer.Web/JD.Writer.Web.csproj", "JD.Writer.Web/"]
COPY ["JD.Writer.ApiService/JD.Writer.ApiService.csproj", "JD.Writer.ApiService/"]
COPY ["JD.Writer.ServiceDefaults/JD.Writer.ServiceDefaults.csproj", "JD.Writer.ServiceDefaults/"]
COPY ["JD.Writer.AppHost/JD.Writer.AppHost.csproj", "JD.Writer.AppHost/"]
COPY ["JD.Writer.E2E/JD.Writer.E2E.csproj", "JD.Writer.E2E/"]

RUN dotnet restore JD.Writer.sln

COPY . .

RUN dotnet publish JD.Writer.Web/JD.Writer.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /app/web \
    /p:UseAppHost=false

RUN dotnet publish JD.Writer.ApiService/JD.Writer.ApiService.csproj \
    --configuration Release \
    --no-restore \
    --output /app/api \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS web
WORKDIR /app
COPY --from=build /app/web .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "JD.Writer.Web.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS web-standalone
WORKDIR /app
COPY --from=build /app/web .
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=ClientOnly
EXPOSE 8080
ENTRYPOINT ["dotnet", "JD.Writer.Web.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /app/api .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "JD.Writer.ApiService.dll"]
