# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore
COPY src/WhatsAppDockerManager/WhatsAppDockerManager.csproj ./WhatsAppDockerManager/
RUN dotnet restore WhatsAppDockerManager/WhatsAppDockerManager.csproj

# Copy source and build
COPY src/WhatsAppDockerManager/ ./WhatsAppDockerManager/
WORKDIR /src/WhatsAppDockerManager
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Install Docker CLI (for managing containers)
RUN apt-get update && apt-get install -y \
    docker.io \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Create directories
RUN mkdir -p /opt/whatsapp-data /app/logs

# Environment variables
ENV ASPNETCORE_URLS=http://+:5000
ENV DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 5000

ENTRYPOINT ["dotnet", "WhatsAppDockerManager.dll"]
