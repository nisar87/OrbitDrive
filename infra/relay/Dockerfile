# OrbitDrive relay container. NOT yet built or run anywhere (container registry was unreachable from the
# build environment); treat as a draft until the gateway workflow builds it.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish services/gateway/OrbitDrive.Gateway -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
# The gateway refuses to start in Production until a real identity verifier (W1) is configured.
ENTRYPOINT ["dotnet", "OrbitDrive.Gateway.dll"]
