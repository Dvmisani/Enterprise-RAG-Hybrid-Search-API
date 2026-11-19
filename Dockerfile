FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/EnterpriseRag.Api/EnterpriseRag.Api.csproj src/EnterpriseRag.Api/
RUN dotnet restore src/EnterpriseRag.Api/EnterpriseRag.Api.csproj
COPY src/EnterpriseRag.Api/ src/EnterpriseRag.Api/
RUN dotnet publish src/EnterpriseRag.Api/EnterpriseRag.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "EnterpriseRag.Api.dll"]
