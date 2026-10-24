FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-arm64v8@sha256:3a05e37c371f358d2b5cb9687d9992171b34b8c9146c283bef865bdbd2c53c55 AS dotnet-runtime

FROM ghcr.io/ersatztv/ersatztv-ffmpeg:8.1.2-6@sha256:a306d00dbcd84938ef48baa22f87e21c2b0e19bdd2859f60495c55e44cb24355 AS runtime-base
COPY --from=dotnet-runtime /usr/share/dotnet /usr/share/dotnet

# https://hub.docker.com/_/microsoft-dotnet
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-arm64v8 AS build
RUN apt-get update && apt-get install -y ca-certificates gnupg
WORKDIR /source

# copy csproj and restore as distinct layers
COPY *.sln .
COPY artwork/* ./artwork/
COPY ErsatzTV/*.csproj ./ErsatzTV/
COPY ErsatzTV.Application/*.csproj ./ErsatzTV.Application/
COPY ErsatzTV.Core/*.csproj ./ErsatzTV.Core/
COPY ErsatzTV.Core.Nullable/*.csproj ./ErsatzTV.Core.Nullable/
COPY ErsatzTV.FFmpeg/*.csproj ./ErsatzTV.FFmpeg/
COPY ErsatzTV.Infrastructure/*.csproj ./ErsatzTV.Infrastructure/
COPY ErsatzTV.Infrastructure.Sqlite/*.csproj ./ErsatzTV.Infrastructure.Sqlite/
COPY ErsatzTV.Infrastructure.MySql/*.csproj ./ErsatzTV.Infrastructure.MySql/
COPY ErsatzTV.Scanner/*.csproj ./ErsatzTV.Scanner/
RUN dotnet restore -r linux-arm64 ErsatzTV/

# copy everything else and build app
COPY ErsatzTV/. ./ErsatzTV/
COPY ErsatzTV.Application/. ./ErsatzTV.Application/
COPY ErsatzTV.Core/. ./ErsatzTV.Core/
COPY ErsatzTV.Core.Nullable/. ./ErsatzTV.Core.Nullable/
COPY ErsatzTV.FFmpeg/. ./ErsatzTV.FFmpeg/
COPY ErsatzTV.Infrastructure/. ./ErsatzTV.Infrastructure/
COPY ErsatzTV.Infrastructure.Sqlite/. ./ErsatzTV.Infrastructure.Sqlite/
COPY ErsatzTV.Infrastructure.MySql/. ./ErsatzTV.Infrastructure.MySql/
COPY ErsatzTV.Scanner/. ./ErsatzTV.Scanner/
WORKDIR /source/ErsatzTV.Scanner
ARG INFO_VERSION="unknown"
ARG BUILD_CONFIG="release"
RUN dotnet publish ErsatzTV.Scanner.csproj --framework net10.0 -c ${BUILD_CONFIG} -o /app --runtime linux-arm64 --no-self-contained --no-restore -p:DebugType=Embedded -p:PublishSingleFile=false -p:PublishTrimmed=false -p:InformationalVersion=${INFO_VERSION}
WORKDIR /source/ErsatzTV
RUN sed -i '/Scanner/d' ErsatzTV.csproj
RUN dotnet publish ErsatzTV.csproj --framework net10.0 -c ${BUILD_CONFIG} -o /app --runtime linux-arm64 --no-self-contained --no-restore -p:DebugType=Embedded -p:PublishSingleFile=false -p:PublishTrimmed=false -p:InformationalVersion=${INFO_VERSION}

# final stage/image
FROM runtime-base
ENV FONTCONFIG_PATH=/etc/fonts
RUN fc-cache update
WORKDIR /app
COPY --from=build /app ./
COPY ersatztv-channel ./
ENV ETV_CONFIG_FOLDER=/config
ENV ETV_TRANSCODE_FOLDER=/transcode
ENTRYPOINT ["./ErsatzTV"]
