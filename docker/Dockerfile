FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-amd64 AS dotnet-runtime

FROM --platform=linux/amd64 ghcr.io/ersatztv/ersatztv-ffmpeg:8.1.2 AS runtime-base
COPY --from=dotnet-runtime /usr/share/dotnet /usr/share/dotnet
RUN apt-get update && \
    apt-get install -y --no-install-recommends python3 python3-pip && \
    python3 -m pip install --target=/app/pythonlibs --no-cache-dir streamlink && \
    apt-get clean -y && \
    rm -rf /var/lib/apt/lists/*

# https://hub.docker.com/_/microsoft-dotnet
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-amd64 AS build
RUN apt-get update && apt-get install -y ca-certificates gnupg default-jre-headless python3-pip
WORKDIR /source

# generate openapi client
COPY ErsatzTV/wwwroot/openapi/. /app/ErsatzTV/wwwroot/openapi/
RUN wget https://repo1.maven.org/maven2/org/openapitools/openapi-generator-cli/7.15.0/openapi-generator-cli-7.15.0.jar
RUN java -jar openapi-generator-cli-7.15.0.jar generate -i /app/ErsatzTV/wwwroot/openapi/scripted-schedule.json -g python -o /app/etv-client --package-name etv_client
RUN rm -rf openapi-generator-cli-7.15.0.jar /app/ErsatzTV
RUN python3 -m pip install --target=/app/pythonlibs /app/etv-client
RUN rm -rf /app/etv-client
COPY scripts/scripted-schedules/. /app/scripted-schedules/

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
RUN dotnet restore -r linux-x64 ErsatzTV/

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
ARG INFO_VERSION="unknown"
ARG BUILD_CONFIG="release"
WORKDIR /source/ErsatzTV.Scanner
RUN dotnet publish ErsatzTV.Scanner.csproj -c ${BUILD_CONFIG} -o /app -r linux-x64 --self-contained false --no-restore /p:DebugType=Embedded /p:InformationalVersion=${INFO_VERSION}
WORKDIR /source/ErsatzTV
RUN sed -i '/Scanner/d' ErsatzTV.csproj
RUN dotnet publish ErsatzTV.csproj -c ${BUILD_CONFIG} -o /app -r linux-x64 --self-contained false --no-restore /p:DebugType=Embedded /p:InformationalVersion=${INFO_VERSION}

# final stage/image
FROM runtime-base
ENV FONTCONFIG_PATH=/etc/fonts
RUN fc-cache update
WORKDIR /app
COPY --from=build /app ./
COPY ersatztv-channel ./
ENV PYTHONPATH=/app/pythonlibs
ENV ETV_CONFIG_FOLDER=/config
ENV ETV_TRANSCODE_FOLDER=/transcode
ENV ETV_DISABLE_VULKAN=1
ENTRYPOINT ["./ErsatzTV"]
