FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

ARG APP_VERSION=local

COPY ["DirectShop.sln", "./"]
COPY ["Server/Server.csproj", "Server/"]
COPY ["Client/Client.csproj", "Client/"]
COPY ["Shared/Shared.csproj", "Shared/"]
RUN dotnet restore "Server/Server.csproj"

COPY . .
RUN dotnet publish "Server/Server.csproj" -c Release -o /app/publish /p:UseAppHost=false /p:InformationalVersion=${APP_VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

RUN mkdir -p /home/data

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Server.dll"]
