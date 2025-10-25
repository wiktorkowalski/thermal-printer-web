# Stage 1: Build React frontend
FROM node:20-alpine AS frontend-build
WORKDIR /app/frontend
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

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
EXPOSE 80
EXPOSE 443
COPY --from=backend-build /app/publish .
ENTRYPOINT ["dotnet", "ThermalPrinterWeb.Api.dll"]
