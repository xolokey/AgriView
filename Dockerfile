# Multi-stage build: Vite frontend + .NET backend

# 1) Frontend build stage
FROM node:20-alpine AS web-build
WORKDIR /app

# Copy only frontend to leverage Docker layer caching
COPY agri-vision-web/package*.json ./agri-vision-web/
WORKDIR /app/agri-vision-web
RUN npm ci

# Copy rest of the frontend and build
COPY agri-vision-web/ /app/agri-vision-web/
# Optional: set API base at build time, can be overridden at runtime via reverse-proxy
# ARG VITE_API_BASE
# ENV VITE_API_BASE=${VITE_API_BASE}
RUN npm run build

# 2) Backend restore/build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS api-build
WORKDIR /src
COPY AgriVision.sln ./
COPY AgriVision.Api/AgriVision.Api.csproj AgriVision.Api/
RUN dotnet restore AgriVision.Api/AgriVision.Api.csproj

# Copy the rest of backend sources
COPY AgriVision.Api/ AgriVision.Api/
RUN dotnet publish AgriVision.Api/AgriVision.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

# 3) Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Copy backend publish output
COPY --from=api-build /app/publish ./

# Copy frontend dist into wwwroot so ASP.NET can serve it
COPY --from=web-build /app/agri-vision-web/dist ./wwwroot

# Expose the port
EXPOSE 8080

# Configure Kestrel to listen on 8080 in containers
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
# You can set OPENAI_API_KEY at runtime; optional mock mode:
# ENV AGRI_MOCK_MODE=false

ENTRYPOINT ["dotnet", "AgriVision.Api.dll"]
