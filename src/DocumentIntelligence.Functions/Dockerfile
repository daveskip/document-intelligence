# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated10.0 AS base
WORKDIR /home/site/wwwroot
EXPOSE 80


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/DocumentIntelligence.Functions/DocumentIntelligence.Functions.csproj", "src/DocumentIntelligence.Functions/"]
COPY ["src/DocumentIntelligence.Infrastructure/DocumentIntelligence.Infrastructure.csproj", "src/DocumentIntelligence.Infrastructure/"]
COPY ["src/DocumentIntelligence.Domain/DocumentIntelligence.Domain.csproj", "src/DocumentIntelligence.Domain/"]
COPY ["src/DocumentIntelligence.Contracts/DocumentIntelligence.Contracts.csproj", "src/DocumentIntelligence.Contracts/"]
COPY ["src/DocumentIntelligence.ServiceDefaults/DocumentIntelligence.ServiceDefaults.csproj", "src/DocumentIntelligence.ServiceDefaults/"]
RUN dotnet restore "./src/DocumentIntelligence.Functions/DocumentIntelligence.Functions.csproj"
COPY . .
WORKDIR "/src/src/DocumentIntelligence.Functions"
RUN dotnet build "./DocumentIntelligence.Functions.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./DocumentIntelligence.Functions.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
# Create and switch to a non-root user for defence-in-depth.
RUN useradd --uid 1001 --create-home funcuser
WORKDIR /home/site/wwwroot
COPY --from=publish /app/publish .
RUN chown -R funcuser:funcuser /home/site/wwwroot
USER funcuser
COPY --from=publish /app/publish .
ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true