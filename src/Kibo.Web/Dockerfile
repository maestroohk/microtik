FROM node:22-alpine AS css
WORKDIR /src
COPY src/Kibo.Web/package.json src/Kibo.Web/package-lock.json ./
RUN npm ci
COPY src/Kibo.Web/Styles ./Styles
COPY src/Kibo.Web/Components ./Components
RUN mkdir -p wwwroot/css wwwroot/fonts \
    && npx @tailwindcss/cli -i Styles/app.css -o wwwroot/css/app.css --minify \
    && cp node_modules/@fontsource-variable/geist/files/geist-latin-wght-normal.woff2 wwwroot/fonts/geist.woff2 \
    && cp node_modules/@fontsource-variable/geist-mono/files/geist-mono-latin-wght-normal.woff2 wwwroot/fonts/geist-mono.woff2

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Kibo.Web/Kibo.Web.csproj src/Kibo.Web/
RUN dotnet restore src/Kibo.Web/Kibo.Web.csproj
COPY src/Kibo.Web/ src/Kibo.Web/
COPY --from=css /src/wwwroot/css src/Kibo.Web/wwwroot/css
COPY --from=css /src/wwwroot/fonts src/Kibo.Web/wwwroot/fonts
RUN dotnet publish src/Kibo.Web/Kibo.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    Kibo__DataProtectionKeysPath=/keys
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Kibo.Web.dll"]
