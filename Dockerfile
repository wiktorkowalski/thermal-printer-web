# Stage 1: Build React frontend
FROM node:20-alpine AS frontend-build
WORKDIR /app/frontend
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ ./
# Override the build output directory for Docker
RUN npm run build -- --outDir=dist

# Stage 2: Build .NET backend
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS backend-build
WORKDIR /src
COPY backend/*.csproj ./
RUN dotnet restore
COPY backend/ ./
# Copy frontend build output to backend wwwroot
COPY --from=frontend-build /app/frontend/dist ./wwwroot
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS final
WORKDIR /app

# Set the port environment variable
ENV PORT=5160
ENV ASPNETCORE_URLS=http://+:5160

# Expose the application port
EXPOSE 5160

COPY --from=backend-build /app/publish .
ENTRYPOINT ["dotnet", "ThermalPrinterWeb.Api.dll"]
