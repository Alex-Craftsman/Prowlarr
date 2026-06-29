FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src
COPY . .
RUN ./build.sh --backend --runtime linux-musl-x64 --framework net8.0

FROM lscr.io/linuxserver/prowlarr:latest

COPY --from=build /src/_output/net8.0/linux-musl-x64/publish/ /app/prowlarr/bin/
