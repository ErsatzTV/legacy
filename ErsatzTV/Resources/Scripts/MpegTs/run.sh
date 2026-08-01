#! /bin/sh

"$ETV_FFMPEG_PATH" -nostdin -threads 1 -hide_banner -loglevel error -nostats -fflags +genpts+discardcorrupt+igndts -readrate 1.0 -i "$ETV_HLS_URL" -map 0 -c copy -metadata service_provider="ErsatzTV" -metadata service_name="$ETV_CHANNEL_NAME" -f mpegts pipe:1
