FROM node:22-bookworm-slim AS web-build
WORKDIR /web
COPY src/Moodle.Web/package.json src/Moodle.Web/package-lock.json ./
RUN npm ci --ignore-scripts --no-audit --no-fund
COPY src/Moodle.Web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Moodle.slnx ./
COPY src/Moodle.Domain/Moodle.Domain.csproj src/Moodle.Domain/
COPY src/Moodle.Application/Moodle.Application.csproj src/Moodle.Application/
COPY src/Moodle.Infrastructure/Moodle.Infrastructure.csproj src/Moodle.Infrastructure/
COPY src/Moodle.Presentation/Moodle.Presentation.csproj src/Moodle.Presentation/
RUN dotnet restore src/Moodle.Presentation/Moodle.Presentation.csproj

COPY src/ src/
COPY public/ public/
COPY --from=web-build /web/dist/ src/Moodle.Presentation/wwwroot/

RUN dotnet publish src/Moodle.Presentation/Moodle.Presentation.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
       curl ca-certificates \
       tesseract-ocr \
       tesseract-ocr-por \
       libleptonica-dev \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:8080
ENV Features__AppV2Enabled=true
EXPOSE 8080

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "Moodle.Presentation.dll"]
