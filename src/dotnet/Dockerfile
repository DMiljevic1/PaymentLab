# Shared Dockerfile for the .NET services. Build with --build-arg PROJECT=Payments|Wallet|Gateway
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src

# Restore first (cached as long as project files do not change)
COPY Directory.Build.props Directory.Packages.props ./
COPY Shared/PaymentLab.Contracts/PaymentLab.Contracts.csproj Shared/PaymentLab.Contracts/
COPY Shared/PaymentLab.ServiceDefaults/PaymentLab.ServiceDefaults.csproj Shared/PaymentLab.ServiceDefaults/
COPY ${PROJECT}/${PROJECT}.csproj ${PROJECT}/
RUN dotnet restore ${PROJECT}/${PROJECT}.csproj

COPY Shared/ Shared/
COPY ${PROJECT}/ ${PROJECT}/
RUN dotnet publish ${PROJECT}/${PROJECT}.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
ARG PROJECT
ENV APP_DLL=${PROJECT}.dll \
    ASPNETCORE_ENVIRONMENT=Production
WORKDIR /app
COPY --from=build /app .
USER app
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\""]
