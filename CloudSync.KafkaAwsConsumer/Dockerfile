# Use the official .NET 8 SDK image to build the app
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY CloudSync.KafkaAwsConsumer/CloudSync.KafkaAwsConsumer.csproj CloudSync.KafkaAwsConsumer/
COPY CloudSync.Core/CloudSync.Core.csproj CloudSync.Core/
COPY CloudSync.Common/CloudSync.Common.csproj CloudSync.Common/
COPY CloudSync.Data/CloudSync.Data.csproj CloudSync.Data/
COPY CloudSync.Infrastructure/CloudSync.Infrastructure.csproj CloudSync.Infrastructure/
COPY NuGet.config ./
RUN dotnet restore CloudSync.KafkaAwsConsumer/CloudSync.KafkaAwsConsumer.csproj
COPY . .
RUN dotnet publish CloudSync.KafkaAwsConsumer/CloudSync.KafkaAwsConsumer.csproj -c Release -o /app/publish

# Use the official .NET 8 runtime image for the app
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
COPY CloudSync.KafkaAwsConsumer/appsettings.json ./
ENTRYPOINT ["dotnet", "CloudSync.KafkaAwsConsumer.dll"] 