# Stage 1: Build React frontend
FROM node:20-alpine AS frontend-build
WORKDIR /app/frontend
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ ./
# Override the build output directory for Docker
RUN npm run build -- --outDir=dist

# Stage 2: Build .NET backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src
COPY backend/*.csproj ./
COPY backend/ThermalPrinterWeb.Tests/*.csproj ./ThermalPrinterWeb.Tests/
# The test project references the API project: one restore covers both.
RUN dotnet restore ThermalPrinterWeb.Tests
COPY backend/ ./
# A red test fails the image build. Before the frontend copy: a frontend change does not run the tests again.
RUN dotnet test ThermalPrinterWeb.Tests -c Release --no-restore
# Copy frontend build output to backend wwwroot
COPY --from=frontend-build /app/frontend/dist ./wwwroot
# After the tests: a new commit does not run them again when the code is the same. Empty in a local build.
ARG GIT_SHA=
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false /p:SourceRevisionId=$GIT_SHA

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Program.cs binds Kestrel to $PORT. Clear the base image's HTTP_PORTS=8080 so ASP.NET does not warn about overriding it.
ENV PORT=5160
ENV ASPNETCORE_HTTP_PORTS=

# Expose the application port
EXPOSE 5160

COPY --from=backend-build /app/publish .
ENTRYPOINT ["dotnet", "ThermalPrinterWeb.Api.dll"]
