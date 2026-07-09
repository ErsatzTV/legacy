# Changelog
All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]
### Added
- Add channel config overlay system for Next engine
  - The channel config schema can be found at https://github.com/ErsatzTV/next/blob/main/schema/channel_config.json
  - Config overlays should be created in the config subfolder `next/channel-config-overlays`
    - A config file named `default.json` will apply to all channels using the Next engine
    - A config file named `{channel_number}.json` (e.g. `1.json`) will apply to the channel with that number
  - Channel overlays will override values from the default overlay which will override values from the FFmpeg Profile
- Add `epg_entries` support to image graphics elements
- Add (date-range based) alternate schedule system for sequential schedules

### Fixed
- Fix HLS Direct playback when JWT auth is also used
- Use configured ffmpeg path for motion and subtitle graphics elements
  - Previously, these elements required ffmpeg to be on PATH
- Fix erroneous warning `Unable to locate MPEG-TS Script in folder Default` on installations with case-sensitive file systems
- Improve speed of motion graphics element compositing
- Fix multiple issues with subtitle handling in local libraries which impacted stream selection
  - Content with multiple sidecar subtitles would incorrectly have the subtitle metadata merged (like language)
  - More rarely, content with an embedded subtitle that has stream index zero would create invalid subtitle records
  - Both cases will automatically be cleaned up during the next local library scan
- Use `und` language tag with sidecar subtitles that have no language in the file name
- Automatically remove already-played playout items from all on demand channels
- Automatically remove already-played playout items from (continuous) scripted schedules
- Extract embedded text subtitles (when enabled in settings) on channels that use custom stream selectors
- Fix subtitle playback using Next streaming engine
- Fix bug where search results for deleted manual collections would continue to appear
- Fix synchronizing subtitle titles from Plex content; this may never have worked previously

### Changed
- Upgrade Intel driver in docker containers to support latest Battlemage devices (e.g. B70)
- Restore release notes section on home page, since bugs continue to be fixed

## [26.5.1] - 2026-05-08
### Fixed
- Fix NVIDIA playback on Linux using legacy streaming engine
- Fix cause of unnecessarily large database when using Emby or Jellyfin libraries
  - This was caused by orphaned actor and actor artwork records, which will be cleaned up hourly
  - When you see logs like `No orphaned actors to delete` and `No orphaned artwork to delete`, you can then reclaim disk space by:
    - Stopping ErsatzTV Legacy
    - Running `sqlite3 ersatztv.sqlite3 'PRAGMA wal_checkpoint(TRUNCATE); VACUUM;'` in your config folder
- Fix Next Engine playout sync when channel is configured to use channel logo as watermark

## [26.5.0] - 2026-05-08
### Added
- Add `Streaming Engine` setting to Channel
  - `Legacy` - (default) uses existing streaming engine
  - `Next` - uses ErsatzTV Next streaming engine
- Add `Next Engine Text Subtitle Mode` setting to Channel
  - `Burn` - (default) will burn all subtitles, including text subtitles
  - `Convert If Possible` - will attempt to convert text subtitles to WebVTT
    - WebVTT subtitles can be dynamically enabled and disabled during playback in supported HLS clients

### Fixed
- Fix `Add Playout` button not opening drop down menu (regression from v26.4.0)
- Consistently apply playout offset when generating XMLTV
- Schedule all required fallback filler in classic schedules
- Don't extract embedded subtitles that have already been extracted

## [26.4.0] - 2026-04-18
### Changed
- Re-branded as `ErsatzTV Legacy`
- Remove BugSnag error reporting integration
- Remove developer's personal Trakt API key
  - Users who want to continue to use Trakt must create an API app and set the `Client ID` as the environment variable `TRAKT__CLIENTID`
- Remove GitHub changelog from home screen

### Fixed
- Support adding trakt lists using `app.trakt.tv` domain (instead of just `trakt.tv`)

## [26.3.0] - 2026-02-24
### Added
- Add log warnings when actual transcoding speed is potentially insufficient to support smooth playback
  - Log messages will include media item id, channel number and transcoding speed
- Add UI language setting to **Settings** > **UI**
  - A small number of translations have been added for `Português (Brasil)` and `Polski`
  - Translation contributions are always welcome!
- Add `Troubleshoot` button to playout details table to show info that may be helpful in determining the source of a playout item
  - Classic schedule info includes schedule, schedule item, scheduler, filler, playback order, random seed, collection index
  - Block schedule info includes block, block item, playback order, random seed, collection index
  - E.g. items with the same random seed are part of the same shuffle
- Add channel setting `Slug Seconds`
  - This controls how many (optional) seconds of black video and silent audio to insert between *every* playout item
  - This will drift playback from the wall clock as slugs are not scheduled in the playout, but are inserted dynamically during playback
  - If this feature turns out to be popular, methods to correct the drift may be investigated
- Add `ETV_INSTANCE_ID` environment variable to disambiguate EPG data from multiple ErsatzTV instances
  - When set, the value will be used in channel identifiers before the final `.ersatztv.org`
- Show warning message when selecting audio format `aac (latm)` for general streaming use when it is only intended for DVB-C

### Changed
- Move dark/light mode toggle to **Settings** > **UI**
- Use latest (non-deprecated) authorization method with Jellyfin API
- Replace direct Discord links with new contact page https://ersatztv.org/contact which also includes other options like Matrix
- Lower GOP size and keyframe interval from four seconds to two seconds in accordance with HLS2 draft spec recommendations

### Fixed
- Improve stability of playback orders `Shuffle` and `Shuffle in Order` over time
- Fix Trakt list sync
- Fix some cases of QSV audio/video desync when *not* seeking by using software decode
  - This only applies to content that *might* be problematic (using a heuristic)
- NVIDIA: force software decode of 10-bit h264 content since hardware decode is unsupported by ffmpeg until version 8
- Graphics engine: fix stream seek value used throughout graphics engine
  - This should fix loading EPG data when used with chapters/mid-roll
  - This should also fix graphics element visibility when using start_seconds on content with chapters/mid-roll
  - This bug was caused by stream seek including the playout item in-point (the chapter start time)
    - Stream seek should only be non-zero when first joining a channel (i.e. in the middle of a playout item or chapter)

## [26.2.0] - 2026-02-02
### Added
- Channel stream selector: add zero-based culture-specific `day_of_week` to `content_condition`, for example:
  - en-US can match sunday using `day_of_week = 0`
  - fr-FR can match sunday using `day_of_week = 6`
  - As a complete example, to match Saturday from 9pm (inclusive) to 11pm (exclusive), based on content start time
    - `content_condition: day_of_week = 6 and (time_of_day_seconds >= 75600 and time_of_day_seconds < 82800)`
- Add `Pad Mode` to ffmpeg profile. Options are:
  - `Hardware If Possible` - default/existing behavior when hardware acceleration is properly configured
  - `Software` - force software padding
    - This can be used to work around buggy GPU driver behavior where padding is green instead of black
    - This is most often seen with VAAPI acceleration (radeonsi or i965 drivers)
- Add API endpoint to clean artwork cache folder (on demand)
  - POST `/api/maintenance/clean_artwork`
- Add health check to warn about unsupported empty (classic) schedules
- Add health check to warn about incompatible ffmpeg due to missing filters
  - This is directly applicable to homebrew `ffmpeg` on MacOS, which is no longer compatible with ErsatzTV
    - `ffmpeg@7` or `ffmpeg-full` should be used instead
- Add `Marathon Group By` option `Director`
  - This groups the *first* director on Movies, Episodes, Music Videos and Other Videos
  - This is supported in classic schedules and sequential schedules
- Add FFmpeg Profile options:
  - `Normalize Audio` (default: true) - normalizes audio streams, or stream copies when disabled
  - `Normalize Video` (default: true) - normalizes video streams, or stream copies when disabled
  - `Normalize Colors` (default: true) - normalizes color parameters when enabled
  - Disabling any of these options may have a significant performance benefit *at the expense of stream stability*
- Add chapter `title` to filler expression
  - This can be used to include or exclude chapters with specific (case-insensitive) titles
  - E.g. `title == 'here'`, `title != 'not here'`, `title like '%here%'`
- Local movie libraries: load fanart from `backdrop` files (created by Jellyfin)

### Changed
- Disable automatic artwork database cleanup
  - This will be re-enabled at some point in the future (after more testing)
  - For now, the API should be used to clean as needed
- Classic Schedules: make multiple `count` an expression
  - The following parameters can be used:
    - `count`: the total number of items in the collection
    - `random`: a random number between zero and (count - 1)
  - For example:
    - `count / 2` will play half of the items in the collection
    - `random % 4 + 1` will play between 1 and 4 items
    - `2` (similar to before this change) will play exactly two items

### Fixed
- Use code signing on all Windows executables (`ErsatzTV-Windows.exe`, `ErsatzTV.exe`, `ErsatzTV.Scanner.exe`)
- Graphics engine:
  - Respect `z_index` (draw order) on all graphics element types
  - Fix bug with `z_index` sorting
- Restore default UI font that was erroneously removed in v26.1.1
- Classic schedules: fix building playouts when `Fill With Group Mode` schedule items also have graphics elements
- Use configured searching log level on startup, instead of the default log level of `Information`
- MySql: fix searching for shows and seasons in schedule items editor
- Fix 500 errors when serving XMLTV due to concurrent file reads and writes
- Fix playback of AC3 audio when targeting stereo output and input layout changes mid-stream
- Use other video artwork in XMLTV template
- Properly update (add or remove) artwork for all local media libraries when files have changed
- Sync Plex library name changes
- Sync Plex episode title, plot, year, date added, release date, episode number changes
- Sync Jellyfin and Emby library name and type changes
  - Library type (movies, shows) can only be changed when synchronization is *disabled* for the library in ETV
- Fix some sequential and scripted playout build failures when using playlists or marathons
- Fix erasing playout items and history so all related data is also erased
  - This includes rerun history, unscheduled gaps, build status
- Fix indexing collections when using Elasticsearch backend

## [26.1.1] - 2026-01-08
### Fixed
- Use code signing on Windows launcher (`ErsatzTV-Windows.exe`) to avoid antivirus false positive

### Changed
- Optimize database check for orphaned artwork
- Include web resources (CSS, JS) locally instead of relying on CDNs

## [26.1.0] - 2026-01-06
### Added
- Graphics Engine:
  - Add `script` graphics element type
    - Supported in playback troubleshooting and all scheduling types
    - Supports arbitrary scripts or executables that output graphics to ETV via stdout
    - Supports EPG and Media Item replacement in entire template
      - EPG data is sourced from XMLTV for the current time
        - EPG data can also load a configurable number of subsequent (up next) entries
      - Media Item data is sourced from the currently playing media item
    - All template data will also be passed as JSON to the stdin stream of the command
    - Template supports:
      - Script and arguments (`command` and `args`)
      - Draw order (`z_index`)
      - Timing (`start_seconds` and `duration_seconds`)
      - Data format (`format`)
        - `raw` format means full frames of BGRA data to stdout
        - `packet` format means ETV graphics packets to stdout
  - Add framerate template data
    - `RFrameRate` - the real content framerate (or channel normalized framerate) as reported by ffmpeg, e.g. `30000/1001`
    - `FrameRate` - the decimal representation of `RFrameRate`, e.g. `29.97002997`
  - Add `Channel_StartTime` template data
    - This indicates the time that the transcode session started for the current channel
- Add remote stream metadata
  - Remote stream definitions (yaml files) can now contain `title`, `plot`, `year` and `content_rating` fields
  - Remote streams can now have thumbnails (same name as yaml file but with image extension)
  - This metadata will be used in generated XMLTV entries, using a template that can be customized like other media kinds
- Add `Download Media Sample` button to playback troubleshooting
  - This button will extract up to 30 seconds of the media item and zip it
- Add `Target Loudness` (LUFS/LKFS) to ffmpeg profile when loudness normalization is enabled
  - Default value is `-16`; some sources normalize to a quieter value, e.g. `-24`
- Add environment variables to help troubleshoot performance
  - `ETV_SLOW_DB_MS` - milliseconds threshold for logging slow database queries (at DEBUG level)
    - e.g. if this is set to `1000`, queries taking longer than 1 second will be logged
  - `ETV_SLOW_API_MS` - milliseconds threshold for logging slow API calls (at DEBUG level)
    - This is currently limited to *Jellyfin*
  - `ETV_JF_PAGE_SIZE` - page size for library scan API calls to Jellyfin; default value is 10
  - `ETV_JF_ENABLE_STATS` - enables logging timing information related to Jellyfin show library scans
- Add `Select All` button to media pages by @Erotemic

### Fixed
- Fix startup on systems unsupported by NvEncSharp
- Fix detection of Plex Other Video libraries using `Plex Personal Media` agent
  - If the library is already detected as a Movies library in ETV, synchronization must be disabled for the library to change it to an Other Videos library
  - A warning will be logged when this scenario is detected
- Graphics Engine:
  - Optimize graphics engine to generate element frames in parallel and to eliminate redundant frame copies
  - Match graphics engine framerate with source content (or channel normalized) framerate
  - Fix loading requested number of epg entries for motion graphics elements
- Fix bug with mirror channels where seemingly random content would be played every ~40 seconds
- Fix chronological sorting for Other Videos that have release date metadata
- Fix playout sorting after using channel number editor
- VAAPI: Only include `-sei a53_cc` flags when misc packed headers are supported by the encoder
  - This should fix playback in some cases, e.g. AMD VAAPI h264 encoder
- AMD VAAPI:
  - work around buggy ffmpeg behavior where hevc_vaapi encoder with RadeonSI driver incorrectly outputs height of 1088 instead of 1080
  - fix green padding when encoding h264 using main profile
- Automatically kill playback troubleshooting ffmpeg process if it hasn't completed after two minutes
- Fix playback of certain BT.2020 content
- Use playlist item count when using a playlist as filler (instead of a fixed count of 1 for each playlist item)
- NVIDIA:
  - Fix stream failure with certain content that should decode in hardware but falls back to software
  - Fix stream failure with content that changes color metadata mid-stream
- Fix stream failure when configured fallback filler collection is empty
- Fix high CPU when errors are displayed; errors will now work ahead before throttling to realtime, similar to primary content
- Fix startup error caused by duplicate smart collection names (and no longer allow duplicate smart collection names)
- Fix erroneous downgrade health check failure with some installations that use MariaDB
- Sequential schedules: fix `count` instruction validation to accept integer (constant) or string (expression)
- Fix multi-part episode grouping logic so that it does NOT require release date metadata for episodes within a single show
  - When **Treat Collections As Shows** is enabled (i.e. for crossover episodes) release date metadata is required for proper grouping
- Fix *many* cases of duplicate names; enforce case-insensitive unique names at the db schema level
- Fix playback when using `ETV_BASE_URL` by @JamesDearlove

### Changed
- No longer round framerate to nearest integer when normalizing framerate
- Allow playlists to have no items included in EPG
- Change how fallback filler works
  - Items will no longer loop; instead, a sequence of random items will be selected from the collection
  - Items may still be cut as needed
  - Hardware acceleration will now be used
  - Items can "work ahead" (transcode faster than realtime) when less than 3 minutes in duration
- Optimize Jellyfin database fields and indexes
- Optimize Jellyfin show library scans by only requesting `People` (actors, directors, writers) when etags don't match
  - This should significantly speed up periodic library scans, particularly against Jellyfin 10.11.x
- Lazy load media item images in UI
- Align alternate schedule and template handling (between classic schedules and block schedules)
  - Both systems now support limiting to a date range
  - This date range can be repeating (when year is not specified for start or end dates)
  - This date range can be exact (when year is specified for start and end dates)

## [25.9.0] - 2025-11-29
### Added
- Show playout warnings count badge in left menu
- Graphics Engine:
  - Add `MediaItem_Resolution` template data (the current `Resolution` variable is the FFmpeg Profile resolution)
  - Add `MediaItem_Start` template data (DateTimeOffset)
  - Add `MediaItem_Stop` template data (DateTimeOffset)
  - Add `ScaledResolution` template data (the final size of the frame before padding)
  - Add `place_within_source_content` (true/false) field to image graphics element
  - Add `name` field to all graphics elements to display in the UI
- Classic and block schedules: add collection type `Search Query`
  - This allows defining search queries directly on schedule items without creating smart collections beforehand
  - As an example, this can be used to filter or combine existing smart collections
    - Filter: `smart_collection:"sd movies" AND plot:"christmas"`
    - Combine: `smart_collection:"old commercials" OR smart_collection:"nick promos"`
- Scripted schedules: add `custom_title` to `start_epg_group`
- Add MPEG-TS Script system
  - This allows using something other than ffmpeg (e.g. streamlink) to concatenate segments back together when using MPEG-TS streaming mode
  - Scripts live in config / scripts / mpegts
  - Each script gets its own subfolder which contains an `mpegts.yml` definition and corresponding windows (batch) and linux (bash) scripts
  - The global MPEG-TS script can be configured in **Settings** > **FFmpeg** > **Default MPEG-TS Script**
- Add `.avs` AviSynth Script support to all local libraries
  - `.avs` was added as a valid extension, so they should behave the same any other video file
  - There are two requirements for AviSynth Scripts to work:
    - FFmpeg needs to be compiled with AviSynth support (not currently available in Docker)
    - AviSynth itself needs to be installed
- Add `Troubleshoot` button to classic schedule list
  - This generates JSON representing the entire schedule which can be shared when requested for troubleshooting
- Add **Settings** > **FFmpeg** > **Probe For Interlaced Frames**
  - When enabled, this will probe *local content* for interlaced frames on demand (immediately before playback)
  - This will be used as a more accurate check for interlaced content
  - The result will be cached (only probed once and stored) in the database along with all other media item statistics (e.g. duration)
  - This feature will currently ignore content that is not streamed from disk
- Add error/offline background customization
  - Default error background is now named `_background.png`
  - Error streams will prioritize using `background.png` if it exists
    - Replacing this `background.png` file will allow custom error/offline backgrounds
- Add `Troubleshoot Playback` buttons on movie and episode detail pages
- Add song background and missing album art customization
  - Default files start with an underscore; custom versions must remove the underscore
- Expose arbitrary EPG data to graphics engine via channel guide templates
  - XML nodes using the `etv:` namespace will be passed to the graphics engine EPG template data
  - For example, adding `<etv:episode_number_key>{{ episode_number }}</etv:episode_number_key>` to `episode.sbntxt` will also add the `episode_number_key` field to all EPG items in the graphics engine
  - All values parsed from XMLTV will be available as strings in the graphics engine (not numbers)
  - All `etv:` nodes will be stripped from the XMLTV data when requested by a client
- Add channel troubleshooting button to channels list
  - This will open the playback troubleshooting tool in "channel" mode
  - This mode requires entering a date and time, and will play up to 30 seconds of *one item from that channel's playout* starting at the entered date and time
- Block schedules: add copy template button to templates table

### Fixed
- Fix HLS Direct playback with Jellyfin 10.11
- Fix remote stream scripts (parsing issue with spaces and quotes)
- Fix block history being removed when it is still needed for mirror channel
  - This caused playout build errors like "Unable to locate history for playout item"
- Fix crashes due to invalid smart collection searches, e.g. `smart_collection:"this collection does not exist"`
- Fix UI crash when editing block playout that has default deco
- Fix playback failure when seeking content with certain DTS audio (e.g. DTS-HD MA)
- Properly set explicit audio decoder on combined audio and video input file
- Fix building sequential schedules across a UTC offset change
- Fix block start time calculation across a UTC offset change
- Fix classic schedule start time calculation across a UTC offset change
- Fix XMLTV generation for channels using on-demand playout mode
- Fix some file not found songs missing from trash view
- Fix error/offline screen generation
- Fix subtitle title sync from Jellyfin libraries
  - Deep scans will be required to update subtitle titles on existing media items
- Fix saving subtitle title changes to the database
  - This fixes e.g. where stream selection would continue to use the original title
  - This fix applies to all libraries (local and media server)
- Fix (3 year old) bug removing tags from local libraries when they are removed from NFO files (all content types)
  - New scans will properly remove old tags; NFO files may need to be touched to force updating during a scan
- Fix bug where looping motion graphics wouldn't be displayed when seeking into second half of content
- Fix `content_total_duration` value in graphics engine opacity expressions
  - This bug caused some graphics elements to display too early after first joining a channel
- Optimize database calls made for search index rebuilds and updates
  - This should improve performance of library scans
- Add toggle to hide/show disabled channels in channel list
- Add disabled text color and `(D)` and `(H)` labels for disabled and hidden channels in channel list
- Graphics engine: fix subtitle path escaping and font loading
- Fix corrupt output (green artifacts) when decoding certain 10-bit content using AMD Polaris GPUs
- Work around sequential schedule validation limit (1000/hr by Newtonsoft.Json.Schema library)
  - Playout builds now use JsonSchema.Net library which has no validation limit
  - Validation tool in the UI still uses Newtonsoft.Json.Schema (with 1000/hr limit) as the error output is easier to understand
- Fix editing scripted and sequential playouts when using MySql
- Fix HLS Direct streams remaining open after client disconnect
- Always log scanner exit code when it is non-zero

### Changed
- Classic schedules: `Refresh` classic playouts from playout list; do not `Reset` them
  - This mode maintains progress; progress can be reset by editing the playout and clicking `Erase Items and History`
- Use smaller batch size for search index updates (100, down from 1000)
  - This should help newly scanned items appear in the UI more quickly
- Replace favicon and logo in background image used for error streams
- Block schedules:
  - Auto scroll day view to block item time when adding and removing block items from template
  - Allow keyboard selection of
    - Block groups in block list
    - Template groups in template list
    - Block groups and blocks in template editor
  - Replace template tree view with searchable table (like blocks)
- Upgrade to dotnet 10

## [25.8.0] - 2025-10-26
### Added
- Graphics engine:
  - Add template data (like `MediaItem_Title`) for other video files
  - Add `MediaItem_Path` for movies, episodes, music videos and other videos
  - Add `get_directory_name` and `get_filename_without_extension` functions for path processing
  - Add `text_align` property to text graphics elements (values: `left`, `right` and `center`)
  - Add `MiddleCenter` value to `location` property on all graphics elements
    - Positive and negative margins can be used to offset from center as desired
  - Add `line_height` property to text element style definition
    - This is a multiplier that defaults to 1.0 when unspecified
  - Add `halo_color`, `halo_width` and `halo_blur` properties to text element style definition
    - These can be used to "outline" text with the configured color (e.g. `#000000`), width (e.g. `10`) and amount of blur (e.g. `2`)
- Add `Block Playout Troubleshooting` tool to help investigate block playout history
- Add sequential schedule file and scripted schedule file names to playouts table
- Add empty (but already up-to-date) sqlite3 database to greatly speed up initial startup for fresh installs
- Add button to copy/clone block from blocks table
- Add playback speed to playback troubleshooting output
  - Speed is relative to realtime (1.0x is realtime)
  - Speeds < 0.9x will be colored red, between 0.9x and 1.1x colored yellow, and > 1.1x colored green
- Add episode thumbnail artwork URL to XMLTV template
  - By default, poster will be added as image with type "poster" and thumbnail will be added as image with type "still"
  - Poster will continue to be added as icon by default
- Add buttons to edit Jellyfin and Emby connection information in **Media Sources** > **Jellyfin** and **Media Sources** > **Emby**
- Add audio format `aac (latm)` for DVB-C compatibility; `aac` uses ADTS by default which is required in most cases
- Add deep scan option for external collections (Plex, Jellyfin, Emby)
  - Jellyfin and Emby collection scans have always been deep scans
  - Now, by default, they will be quick scans that trust Jellyfin and Emby's etags for detecting changes
  - If a quick scan misses updating a collection, deep scans can be triggered manually

### Fixed
- Fix NVIDIA startup errors on arm64
- Fix remote stream durations in playouts created using block, sequential or scripted schedules
- Fix playback troubleshooting selecting a subtitle even with no subtitle stream selected in the UI
- Fix intermittent watermark opacity
- Improve reliability of live remote streams; they should transcode closer to realtime in most cases
- Dramatically improve stream startup time
- VAAPI: fix scaling image-based subtitles (e.g. dvdsub)
- VAAPI: fix overlaying picture subtitles with scaling behavior crop
- Fix HLS Segmenter (fmp4) on Windows
- Playback troubleshooting: wait for at least 2 initial segments (up to configured initial segment count) to reduce stalls
- Fix Trakt List sync
- Fix QSV audio sync
- Fix QSV capability detection on Linux using non-drm displays (e.g. wayland)
- Fix playlist filtering bug that made HLS Segmenter more likely to fail when streaming for multiple hours
- Fix NVIDIA overlaying text subtitles and permanent watermark on 10-bit content
- Fix UI error adding deco
- Fix UI error editing watermarks and graphics elements on blocks
- Fix showing playout build failure details when resetting a playout
- Fix scheduling auto-generated trakt list playlists that contain shows
- Fix playout builder getting stuck (forever) on block item with an empty collection
- Fix HLS Direct playback when using custom stream selector or preferred audio language/title
- Fix selecting embedded subtitles (text and picture) with HLS Direct
- Fix building scripted schedules across a UTC offset change

### Changed
- Do not use graphics engine for single, permanent watermark
- Rename `YAML Validation` tool to `Sequential Schedule Validation`
- Greatly reduce debug log spam during playout builds by logging summaries of certain warnings at the end
- Remove *experimental* `HLS Segmenter V2` streaming mode; it is not possible to maintain quality output using this mode
- Remove *experimental* `HLS Segmenter (fmp4)` streaming mode; this mode only worked properly in a browser, many clients did not like it
- Change how scanner process and main process communicate, which should improve reliability of search index updates when scanning

## [25.7.1] - 2025-10-09
### Added
- Add search field to filter blocks table
- Show full error/exception details in playback troubleshooting logs
- Add basic free space validation on startup
  - ETV will now fail to start with less than 128 MB free space in config or transcode folders
- Add downgrade health check to inform users when they are doing something that WILL impact stability

### Fixed
- Do not allow deleting ffmpeg profiles that are used by channels
- Do not allow deleting default ffmpeg profile
- Allow ffmpeg profiles using VAAPI accel to set h264 video profile
- Fix HLS Direct playback, and make it accessible on separate streaming port
- Fix playback troubleshooting when using multiple watermarks or multiple graphics elements

### Changed
- Use table instead of tree view on blocks page
- Use different release packaging system to workaround false positive from Windows Defender

## [25.7.0] - 2025-10-03
### Added
- Add new collection type `Rerun Collection`
  - This collection type will show up as *two* collection types in classic schedules
    - `Rerun (First Run)`
    - `Rerun (Rerun)`
  - The playback order for each of these collection types can be set on the rerun collection itself
    - e.g. `Season, Episode` order for first run, `Shuffle` for rerun
  - When a first run item is added to a playout, it will immediately be made available in the rerun collection
  - Rerun history is currently scoped to the playout, and only supported in classic schedules
    - This means resetting the playout will reset the rerun history
  - Items will still be scheduled from the rerun collection if it is used before the first run collection
    - Otherwise, the rerun collection would be considered "empty" which prevents the playout build altogether
- Add `Rkmpp` hardware acceleration by @peterdey
  - This is supported using jellyfin-ffmpeg7 on devices like Orange Pi 5 Plus and NanoPi R6S
- Block schedules: allow selecting multiple watermarks on block items
- Block schedules: allow selecting multiple graphics elements on block items
- Add `motion` graphics element type
  - Supported in playback troubleshooting and all scheduling types
  - Supports video files with alpha channel (e.g. vp8/vp9 webm, apple prores 4444)
  - Supports EPG and Media Item replacement in entire template
    - EPG data is sourced from XMLTV for the current time
      - EPG data can also load a configurable number of subsequent (up next) entries
    - Media Item data is sourced from the currently playing media item
  - Template supports:
    - Content (`video_path`)
    - Placement (`location`, `horizontal_margin_percent`, `vertical_margin_percent`)
    - Scaling (`scale`, `scale_width_percent`)
    - Timing (`start_seconds`)
    - End behavior (`end_behavior`)
      - `disappear` (default) - disappear after playing once
      - `loop` - loop forever
      - `hold` - hold last frame forever, or `hold_seconds`
    - Draw order (`z_index`)
- Add search fields to filter collections, schedules and playouts tables
- Add selected row background color to schedules and playouts tables
- Graphics engine text element: add `width_percent` and `text_fit` to support wrapping and scaling text
  - `text_fit: none` or unspecified will keep existing behavior (render text exactly as configured)
  - `text_fit: wrap` will wrap text to the given `width_percent`
  - `text_fit: scale` will scale text *smaller* to fit the given `width_percent`
    - Text that already fits with the configured style will not be adjusted
- Block schedules: add **experimental** `Break Content` to decos
  - Break content is similar to filler from classic schedules
  - Break content is currently limited to placement `Block Start` (play before anything else in the block)
    - Future work will add other placement options
  - Break content is currently limited to playlists (which do *not* pad - they simply play through the playlist one time)
    - Future work will add other collection options which will pad to the full block duration
- Add page to reorder channels (edit channel numbers) using drag and drop
  - New page is at **Channels** > **Edit Channel Numbers**
- Scripted schedules: add setting to configure timeout of scripted playout build
  - New setting is at **Settings** > **Playout** > **Scripted Schedule Timeout**
- Add *experimental* streaming mode `HLS Segmenter (fmp4)`
  - This mode is required for better compliance with HLS spec, and to support new output codecs
  - This mode *will replace* `HLS Segmenter` when it has received more testing
- Allow HEVC playback in channel preview
  - This is restricted to compatible browsers
  - Preview button will be red when preview is disabled due to browser incompatibility
- Add AV1 encoding support with NVIDIA, VAAPI and QSV acceleration
  - This also requires `HLS Segmenter (fmp4)`
- Add `Stream Selector` option to playback troubleshooting tool
  - This can be helpful for validating stream selector behavior with specific content
  - Manual subtitle selection will be disabled when using a stream selector
- Add basic log viewer to playback troubleshooting tool
  - Streaming log level will be forced to `Debug` during troubleshooting
  - Streaming log level will be restored to its previous value after troubleshooting completes
- Add playout build status to UI
  - Playouts that fail to build will be highlighted yellow in the playouts table
  - Clicking on the failed playout will display the warning or error that caused the playout build to fail

### Fixed
- Fix green output when libplacebo tonemapping is used with NVIDIA acceleration and 10-bit output in FFmpeg Profile
- Fix playback when invalid video preset has been saved in FFmpegProfile
  - This can happen when NVIDIA accel falls back to libx264 software encoder for 10-bit h264 output
- Fix 10-bit output when using NVIDIA and graphics engine (watermark or other overlays)
- Fix playback of Jellyfin content with unknown color range
- Block schedules: skip collections (block items) that will never fit in block duration
- Block schedules: skip media items that will never fit in block duration
- Fix HLS playlist generation for clients that actually care about discontinuities (like hls.js)
  - This should resolve most playback issues with built-in channel preview
- Fix deco dead air fallback selection and duration on mirror channels
- Fix fallback filler duration on mirror channels
- Fix slow startup caused by check for overlapping playout items
- Fix green line in *most* cases when overlaying content using NVIDIA acceleration and H264 output
- Fix non-SRT (e.g. SSA/ASS) external subtitle playback from media servers
- Fix extracted text subtitle playback from media servers
- Fix extracted text subtitles getting into invalid state after media server deep scans
  - Targeted deep scans will now extract text subtitles for the scanned show
- Fix playlist preview
- Use NVIDIA NvEnc API to detect encoder capability instead of heuristic based on GPU model/architecture
- Use NVIDIA Cuvid API to detect decoder capability instead of heuristic based on GPU model/architecture
- Fix filler expression not being respected when using a playlist as filler
- Use "repeat count" metadata from animated GIFs in graphics engine (i.e. watermarks)
  - GIFs flagged to loop forever will loop forever
  - GIFs with a specific loop count will loop the specified number of times and then hold the final frame
    - Note that looping is relative to the start of the content, so this works best with permanent watermarks
- Fix some more hls.js warnings by adding codec information to multi-variant playlists
- Fix hardware decode of h264 constrained baseline content using VAAPI accel
- Custom stream selector: ignore embedded text subtitles that have not been extracted
- Fix cropping Jellyfin and Emby content that is smaller than the crop resolution
- Sync movies with non-file media sources (e.g. http/nfs) from Emby movie libraries by @jasonarends

### Changed
- Filler presets: use separate text fields for `hours`, `minutes` and `seconds` duration
- Use autocomplete fields for collection searching in deco editor
  - This greatly improves the editor performance

## [25.6.0] - 2025-09-14
### Added
- Classic schedules: allow selecting multiple graphics elements on schedule items
- Block schedules: allow selecting multiple graphics elements on decos
- Add channel `Playout Source` setting
  - `Generated`: default/existing behavior where channel must have its own playout
  - `Mirror`: channel will play content from the specified `Mirror Source Channel`'s playout
    - This allows the exact same content on different channels with different channel settings
    - `Playout Offset` can be used to offset the times of scheduled playout items from the mirror source channel
      - e.g. -2 hours will cause the mirror channel to play content 2 hours before the mirror source channel
- Add support for `.aif`, `.aifc`, `.aiff` song files
- Classic schedules: add playback order `Marathon`
  - This can be used with collections and smart collections
  - Items from the collection will be grouped by the `Marathon Group By` setting: `Artist`, `Album`, `Season` or `Show`
  - The order of groups can optionally be shuffled
  - The order of items in each group can optionally be shuffled (otherwise `Season, Episode` or `Chronological` as appropriate)
  - A batch size can be set to limit the number of items to schedule from each group at a time
    - Empty or zero batch size means play all items from each group before advancing
    - Any other value means play the specified number of items before advancing to the next group
- Log API requests when `Request Logging Minimum Log Level` is set to `Debug`
- Add `Count` setting to each playlist item
  - Previously, when `Play All` was unchecked, this was implicitly 1
  - Now, the playlist can play a specific number of items from the collection before moving to the next playlist item
- Classic schedules: add `Shuffle Playlist Items` setting to shuffle the order of playlist items
  - Shuffling happens initially (on playout reset), and after all items from the *entire playlist* have been played
- Add playout detail row coloring by @peterdey
  - Filler has unique row colors
  - Unscheduled gaps are now displayed and have a unique row color
- Process entire graphics element YAML files using scriban
  - This allows things like different images based on `MediaItem_ContentRating` (movie) or `MediaItem_ShowContentRating` (episode)
- Playlists: add playback order `Shuffle In Order` for collections and smart collections

### Fixed
- Fix transcoding content with bt709/pc color metadata
- Fix scripted schedule validation (file exists) when creating or editing playout
- Fix adding single episode, movie, season, show to empty playlists
- Fix startup with MySql as non-superuser
  - `local_infile=ON` is required when using MySQL (for bulk inserts when building playouts)
  - ETV will set this automatically when it has permission
  - When ETV does not have permission, startup will fail with logged instructions on how to configure MySql
- Fix scaling anamorphic content in locales that don't use period as a decimal separator (e.g. `,`)
- Block schedules: fix playout build crash when empty collection uses random playback order
- Fix watermarks and graphics elements on primary content split by mid-roll filler
- Fix watermarks and graphics elements when `Scaling Behavior` is `Crop`
- Fix hardware acceleration health check message on mobile
- Fix deco selection logic
- Fix inefficient database migration that would cause database initialization to get stuck
- Classic schedules: fix scheduling behavior when a flood item is before a flexible fixed start item
  - Sometimes the flood item wouldn't schedule anything
- Fix troubleshooting certain text graphics elements by generating fake EPG data

### Changed
- **BREAKING CHANGE**: change how `Scripted Schedule` system works
  - No longer uses embedded python (IronPython); instead uses HTTP API
  - OpenAPI Description has been added at `/openapi/scripted-schedule.json`
    - This allows scripted scheduling from *many* languages
  - The scripted schedule file must now be directly executable (though a wrapper can be used to load a venv)
  - The scripted schedule file will be passed the following arguments (in order):
    - The API host (e.g. `http://localhost:8409`)
    - The build id (a UUID string that is required on all API calls)
    - The playout build mode (e.g. `reset` or `continue`, normally only used for specific logic when resetting a playout)
  - Custom arguments can be included in the `Scripted Schedule` field in the playout editor
    - Custom arguments will be passed *after* required arguments
    - For example, a `Scripted Schedule` of `/home/jason/schedule.sh "party central" 23` will be executed like
      - `/home/jason/schedule.sh http://localhost:8409 00000000-0000...0000 reset "party central" 23`
    - This enables wrapper script re-use across multiple scripted schedules
  - API reference is available at `/docs`
  - Docker images contain pre-generated python api client and entrypoint script
    - Entrypoint is at `/app/scripted-schedules/entrypoint.py`
    - Scripts folder should be mounted to `/app/scripted-schedules/scripts`
    - Playouts should be created with scripted schedule `/app/scripted-schedules/entrypoint.py script-name` (no trailing `.py`)
- Automatically ignore Specials/Season 0 when using `Season, Episode` playback order

## [25.5.0] - 2025-09-01
### Added
- Add *experimental* graphics engine
  - All watermarks will use new graphics engine
- Add `Opacity Expression` watermark mode
  - This allows specifying an expression that returns an opacity between 0.0 and 1.0
  - The expression can use:
    - `content_seconds` - the total number of seconds the frame is into the content
    - `content_total_seconds` - the total number of seconds in the content
    - `channel_seconds` - the total number of seconds the frame is from when the channel started/activated
    - `time_of_day_seconds` - the total number of seconds the frame is since midnight
  - The expression can also use functions:
    - `LinearFadeDuration(time, start, fadeSeconds, peakSeconds)`
    - `LinearFadePoints(time, start, peakStart, peakEnd, end)`
- Add `Z-Index` to watermark editor
  - The graphics engine will order by z-index when overlaying watermarks
- Add *experimental* `Graphics Element` template system
  - Graphics elements are defined in YAML files inside ETV config folder / templates / graphics-elements subfolder
  - Add `text` graphics element type
    - Supported in playback troubleshooting and YAML playouts
    - Displays multi-line text in a specified font, color, location, z-index
    - Supports constant opacity and opacity expression
    - Supports EPG and Media Item variable replacement
      - EPG data is sourced from XMLTV for the current time
        - EPG data can also load a configurable number of subsequent (up next) entries
      - Media Item data is sourced from the currently playing media item
  - Add `image` graphics element type
    - Supported in playback troubleshooting and YAML playouts
    - Displays an image, similar to a watermark
    - Supports constant opacity and opacity expression
  - Add `subtitle` graphics element type
    - Supported in playback troubleshooting and YAML playouts
    - Supports SRT and SSA/ASS subtitle formats
    - Supports EPG and Media Item variable replacement
      - EPG data is sourced from XMLTV for the current time
        - EPG data can also load a configurable number of subsequent (up next) entries
      - Media Item data is sourced from the currently playing media item
- YAML playout: add `graphics_on` and `graphics_off` instructions to control graphics elements
  - `graphics_on` requires the name of a graphics element template, e.g. `text/cool_element.yml`
    - The `variables` property can be used to dynamically replace text from the template
  - `graphics_off` will turn off a specific element, or all elements if none are specified
- Add `Seek Seconds` to playback troubleshooting to support capturing timing-related issues
- Custom stream selector: add `content_condition` to allow channel and time-of-day based decisions
  - `content_condition` expression can use
    - `channel_number`
    - `channel_name`
    - `time_of_day_seconds` - the start time for the current item, represented in seconds since midnight
- Add support for external chapter files next to video files
  - Currently supports Matroska Chapter XML format
    - Chapter files have .xml or .chapters extension
- Add targeted (single-show) library scanning
  - Supports quick and deep scans
  - Can be triggered from the `Scan` button on show pages
  - Can be triggered by API call to `/api/libraries/{library-id}/scan-show`
- Add XMLTV setting `XMLTV Block Behavior` to control how block schedules appear in the EPG
  - `Split Time Evenly` - default (existing) behavior; block time is split among all items that are visible in the EPG
  - `Use Actual Times` - actual times are used for all items that are visible in the EPG
    - This will introduce EPG gaps when filler is used, or when items are hidden from the EPG
- Add *experimental* `Scripted Schedule` playout system
  - This system uses python scripts to support the highest degree of customization
  - The goal is to expose methods equivalent to all sequential schedule (YAML) instructions
- YAML and Scripted schedules: add `offline_tail` and `stop_before_end` to `pad_to_next` instruction
  - Both parameters default to `true`

### Fix
- Fix database operations that were slowing down playout builds
  - YAML playouts in particular should build significantly faster
- Fix channel playout mode `On Demand` for Block and YAML schedules
- Fix QSV transitions when remote streaming from a media server
- Fix green output when padding with VAAPI accel and i965 driver
- Fix watermark custom image validation
- Fix playback when using any watermarks that were saved with invalid state (no image)
- Fix overlapping block playout items caused by `Stop scheduling block items` value `After Duration End`
  - Existing overlapping items will not be removed, but no new overlapping items will be created
  - Until these existing items age out, there will be warnings logged after each playout build/extension
- Fix playback of anamorphic content from Jellyfin
  - This fix requires a manual deep scan of any affected Jellyfin library
- Fix bug where multiple Plex servers would mix their episodes
- Fix incorrect media item counts after removing paths from local libraries
- Fix song playback in playback troubleshooting
- Fix seeking into extracted text subtitles
- Fix error when changing default (lowest priority) alternate schedule
- Fix remote library editing, tv shows, artists with MySql/MariaDB
- Classic schedules: fix alternate schedule transitions (some edge cases would cause days to be skipped completely)
- Classic schedules: always start new alternate schedules with the first schedule item
- Classic Schedules: log offline gaps longer than 1 hour due to strict fixed start times
- Fix `HLS Segmenter V2` streaming mode with AMF acceleration
- Fix `HLS Segmenter V2` streaming mode with VideoToolbox acceleration
- Fix startup process for database and search index initialization
  - Redirect all pages to home page when initializing to prevent errors
  - Clear stale sqlite migration lock on startup to prevent getting stuck on database initialization
- Fix display of long season placeholder text (when season posters are unavailable)

### Changed
- Rename some schedule and playout terms for clarity
  - Schedules are used to build playouts and are what actually differs
  - The playout is the end result, and is the same no matter what schedule kind is used
  - Supported schedule kinds:
    - `Classic Schedules`
    - `Block Schedules`
    - `Sequential Schedules` (formerly `YAML Schedules` or `YAML Playouts`)
    - `Scripted Schedules`
    - `JSON (dizqueTV) Schedules` (formerly `External JSON Playouts`)
- Allow multiple watermarks in playback troubleshooting
- Classic schedules: allow selecting multiple watermarks on schedule items
- Block schedules: allow selecting multiple watermarks on decos
- Block schedules: change available watermark modes on decos. For reference, the levels from highest to lowest with block schedules are `Global` > `Channel` > `Playout Default Deco` > `Template Deco`.
  - `Inherit` - Use watermarks configured at a higher level
  - `Disable` - Disable watermarks at this level and above
  - `Replace` - Replace all watermarks configured at a higher level with those on this deco
    - This was renamed from `Override`
  - `Merge` - Merge all watermarks configured at a higher level with those on this deco
- YAML playout: `watermark` instruction changes:
  - When value is `true`, will add named watermark to list of active watermarks
  - When value is `false` and `name` is specified, will remove named watermark from list of active watermarks
  - When value is `false` and `name` is not specified, will clear all active watermarks
- Use consistent UI sorting and validation, and fix renaming errors for
  - Block groups, blocks
  - Template groups, templates
  - Deco groups, decos
  - Deco template groups, deco templates

## [25.4.0] - 2025-08-05
### Added
- Add `Troubleshoot Playback` to overflow menu on all media cards
  - This should eliminate the need to lookup media ids for content
- Add subtitle selection to playback troubleshooting. This is limited to:
  - Sidecar text subtitles (e.g. `srt` files)
  - Embedded image subtitles
  - Embedded text subtitles that have already been extracted by ETV
- Add light mode and light/dark mode toggle to app bar
- YAML playout: add `pre_roll` instruction to enable and disable a pre-roll sequence
  - With value of `true` and `sequence` property, will enable automatic pre-roll for all content in the playout to the sequence with the provided key
  - With value of `false`, will disable automatic pre-roll in the playout
- YAML playout: add `post_roll` instruction to enable and disable a post-roll sequence
  - With value of `true` and `sequence` property, will enable automatic post-roll for all content in the playout to the sequence with the provided key
  - With value of `false`, will disable automatic post-roll in the playout
- YAML playout: add `mid_roll` instruction to enable and disable a mid-roll sequence
  - With value of `true` and `sequence` property, will enable automatic mid-roll for (`count` and `all`) content in the playout to the sequence with the provided key
  - With value of `false`, will disable automatic post-roll in the playout
  - `expression` can be used to influence which chapters are selected for mid roll (same as in filler preset)
- YAML playout: add `rewind` instruction to set start of playout relative to the current time
  - Value should be formatted as `HH:MM:SS` e.g. `00:05:30` for 5 minutes 30 seconds (before now)
  - This is instruction is mostly useful for debugging transitions, and can only be used as a reset instruction
- YAML playout: add `import` section to allow importing partial YAML definitions that include `content` and `sequence` entries
- Add YAML playout validation (using JSON Schema)
  - Invalid YAML playout definitions will fail to build and will log validation failures as warnings
  - `content` is fully validated
  - `sequence` is fully validated
  - `reset` is fully validated
  - `playout` is fully validated
- Add `Playlist` collection type to filler presets
  - This will force filler mode `Count`
  - Whenever the filler is used, it will schedule `Count` times full time through the playlist
    - If the playlist has 3 items and none set to play all, it will schedule 3 items when `Count = 1`
    - If the playlist has 3 items and none set to play all, it will schedule 6 items when `Count = 2`
  - Using the same playlist in the same schedule for anything other than filler may cause undesired behavior
- Detect supported VideoToolbox hardware decoders and encoders
  - Software decoders/encoders will automatically be used when hardware versions are unavailable
- Add VideoToolbox Capabilities to Troubleshooting page
- Add `Use Chapters As Media Items` option to filler preset
  - This option allows scheduling individual chapters as filler
  - The chapters are shuffled or otherwise sorted together just like normal filler would be
- Add smart collection edit page to allow renaming smart collections
  - Previous edit link behavior (performing search using smart collection query) now uses magnifying glass icon
- Add channel `Transcode Mode` setting
  - This setting is currently disabled and only has the value `On Demand`
- Add channel `Idle Behavior` setting to control the transcoding behavior after all clients have disconnected
  - `Stop On Disconnect` - stops the transcoder after all clients have disconnected + the global idle timeout
  - `Keep Running` - transcoder will run until manually stopped
- Add support for music video thumbnails that end in `-thumb`
  - For example `Music Video.mkv` could have a corresponding thumbnail `Music Video-thumb.jpg`
- Reorganize troubleshooting page
  - Add `YAML Validation` tool in `Troubleshooting` > `Tools`

### Fixed
- Fix app startup with MySql/MariaDB
- YAML playout: fix `pad_to_next` always running over time
- Fix playback with text subtitles when seeking into content, i.e. when first joining a channel
- Fix playback with `.ass` and `.ssa` text subtitles
- Fix green padding with 10-bit source content and i965 VAAPI driver
- Fix building playouts with empty schedules
- Fix schedule start time calculation when daily playout build goes beyond midnight and into a different alternate schedule
- Fix compatibility with older NVIDIA devices (compute capability 3.0+) in unified docker image
- Fix transitions when using NVIDIA, QSV and VAAPI acceleration
- Fix playback of remote streams on channels where framerate normalization is enabled

### Changed
- Always tell ffmpeg to stop encoding with a specific duration
  - This was removed to try to improve transitions with ffmpeg 7.x, but has been causing issues with other content
- Move search debug logging to its own log category; add `Searching Minimum Log Level` to `Settings` > `Logging`
- Classic schedules: always schedule the full `Duration` amount instead of stopping mid-duration
  - This allows duration items to be scheduled beyond midnight
  - e.g. fixed start time 22:00 with 4 hour duration will schedule until 02:00 instead of stopping at midnight
- Rename channel setting `Progress Mode` to `Playout Mode`
  - This controls the progression of the channel's playout, and has nothing to do with transcoding
  - `Always` is now called `Continuous` (playout progresses with wall clock)
  - `On Demand` is unchanged (playout only progresses while a client is watching the channel)
- Replace channel `Active Mode` setting with new `Is Enabled` and `Show In EPG` settings
  - `Active` channels will be converted to `Is Enabled` = true and `Show In EPG` = true
  - `Hidden` channels will be converted to `Is Enabled` = true and `Show In EPG` = false
  - `Inactive` channels will be converted to `Is Enabled` = false and `Show In EPG` = false

## [25.3.1] - 2025-07-24
### Fixed
- Fix fallback filler playback

## [25.3.0] - 2025-07-24
### Added
- Add new channel stream (audio and subtitle) selector system
  - Channel editor has a new field `Stream Selector Mode`
    - `Default` maintains existing behavior
    - `Custom` uses a YAML config file
  - The YAML config contains a prioritized list of stream selector "items" (audio and subtitle criteria pairs)
  - The items are tested against the media from top to bottom, and when (at least) a matching audio track is found, stream selection occurs
  - As an example, the custom stream selector config can specify (in priority order):
    - english audio (and disable subtitles)
    - any other audio (and english subtitles, if they exist)
  - Criteria can include
    - Stream language
    - Stream title (allowed title and/or blocked title)
    - Stream condition, which is an expression that can use
      - `id` (index)
      - `title`
      - `lang`
      - `default`
      - `forced`
      - `sdh` (subtitle only)
      - `external` (subtitle only)
      - `codec`
      - `channels` (audio only)
    - An example subtitle condition: `lang like 'en%' and external`
    - An example audio condition: `title like '%movie%' and channels > 2`
- Add new channel setting `Active Mode`
  - `Active` - default value, channel streams as normal and has normal visibility
  - `Hidden` - channel streams as normal and is hidden from M3U/XMLTV/HDHR
  - `Inactive` - channel cannot stream (will 404) and is hidden from M3U/XMLTV/HDHR
- Synchronize Plex "network" metadata for Plex show libraries
  - Shows will have new `network` search field
  - Episodes will have new `show_network` search field
- YAML playout: add `stop_before_end` setting to `pad_until` and `duration` instructions
  - When `stop_before_end: false`, content can run over the desired time before executing the next instruction
- YAML playout: add `offline_tail` setting to `pad_until` instruction
  - This can be used to stop primary content before the desired time (`stop_before_end: true` and `offline_tail: false`)
  - You can then have a second `pad_until` with the same target time and different content
- YAML playout: make `tomorrow` an expression on `pad_until` instruction
  - `true` and `false` still work as normal
  - The current time (as a decimal) can also be used in the expression, e.g. `now > 23`
    - `now = hours + minutes / 60.0 + seconds / 3600.0`
    - So `10:30 AM` would be `10.5`, `10:45 PM` would be `22.75`, etc
- YAML playout: make `skip_items` an expression
  - The following parameters can be used:
    - `count`: the total number of items in the content
    - `random`: a random number between zero and (count - 1)
  - For example:
    - `count / 2` will start in the middle of the content
    - `random` will start at a random point in the content
    - `2` (similar to before this change) will skip the first two items in the content
- YAML playout: make `count` an expression
  - The following parameters can be used:
    - `count`: the total number of items in the content
    - `random`: a random number between zero and (count - 1)
  - For example:
    - `count / 2` will play half of the items in the content
    - `random % 4 + 1` will play between 1 and 4 items
    - `2` (similar to before this change) will play exactly two items
- YAML playout: add `disable_watermarks` property to all content instructions
  - This property defaults to `false` (meaning watermarks are allowed by default)
  - Setting to `true` will prevent watermarks from ever appearing over the content
- YAML playout: add `watermark` instruction
  - With value of `true` and `name` property, will override the watermark in the playout to the watermark with the provided name
  - With value of `false`, will restore default watermark value (channel watermark, global watermark)
- Show health check warning and error badges in nav menu
- Add `Expression` for mid-roll filler to allow custom logic for using or skipping chapter markers
  - The following parameters can be used:
    - `total_points`: total number of potential mid-roll points
    - `matched_points`: number of mid-roll points that have already matched the expression
    - `total_duration`: total duration of the content, in seconds
    - `total_progress`: normalized position from 0 to 1
    - `last_mid_filler`: seconds since last mid-roll filler
    - `remaining_duration`: duration of the content after this mid-roll point, in seconds
    - `point`: the position of the mid-roll point, in seconds
    - `num`: the mid-roll point number, starting with 1
- Add `Disable Watermarks` checkbox to block items
  - Block items that have this checked will never display a watermark, even with Deco set to override watermark
- Add `ETV_MAXIMUM_UPLOAD_MB` environment variable to allow uploading large watermarks
  - Default value is 10
- Update ffmpeg health check to link to ErsatzTV-FFmpeg release that contains binaries for win64, linux64, linuxarm64
- Add `Playback Troubleshooting` page
  - This tool lets you play specific content without needing a test channel or schedule
  - You can specify
    - The media item id (found in ETV media info, and ETV movie URLs)
    - The ffmpeg profile to use
    - The watermark to use (if any)
  - Clicking `Play` will play up to 30 seconds of the specified content using the desired settings
  - Clicking `Download Results` will generate a zip archive containing:
    - The FFmpeg report of the playback attempt
    - The media info for the content
    - The `Troubleshooting` > `General` output
- Support `(Part [english number])` name suffixes for multi-part episode grouping, for example:
  - `Awesome Episode (Part One)`
  - `Better Episode (Part Two)`
  - `Not So Great (Part Three)`
- Add Trakt List option `Auto Refresh` to automatically update list from trakt.tv once each day
- Add Trakt List option `Generate Playlist` to automatically generate ETV Playlist from matched Trakt List items
- Read `country` field from movie NFO files and include in search index as `country`
- Add *experimental* and *incomplete* `Remote Stream` library kind
    - Remote Stream libraries have fallback metadata added like Other Video libraries (every folder is a tag)
    - Remote Stream library items consist of YAML (`.yml`) files with the following fields
      - `url`: the URL of the content that can be played directly by ffmpeg
      - `script`: the process name and arguments for a command that will output content to stdout
      - `is_live`: *required* property that indicates whether the remote stream contains live content
          - When this is set to `true`, ETV cannot work ahead on transcoding this item, which is a necessary tradeoff for supporting live content
          - When this is set to `false`, ETV will treat the stream as VOD and attempt to work ahead on transcoding like any other local item
              - This *will* cause errors when the content is actually live, so it's important to configure this correctly
      - `duration`: when the content is live and does not have duration metadata, this must be provided to allow scheduling
    - The remote stream definition (YAML file) may provide either a `url` or a `script`
      - If both are provided, `url` will be used
- Include number of chapters in search index as `chapters`

### Changed
- Allow `Other Video` libraries and `Image` libraries to use the same folders
- Try to mitigate inotify limit error by disabling automatic reloading of `appsettings.json` config files
- Support `movie`, `musicvideo` and `episodedetails` top-level tags in other video NFO files
  - Note that no change has been made to the metadata tags that are actually parsed, but this should help with various types of content
- Remove some limits on multithreading that are no longer needed with latest ffmpeg
  - Mixed transcoding (software decode, hardware filters/encode) can now use multiple decode threads
- Split main `Settings` page into multiple pages
- Update UI layout on all pages to be less cramped and to work better on mobile
- Add CPU and Video Controller info to `Troubleshooting` > `General` output
- Enable write-ahead logging (WAL) mode on SQLite databases
- Add `Multiple Mode` option to schedule items editor and remove support for count values of zero
  - `Count`: same behavior as before, requires a number of media items to play and will always schedule the same number
  - `Collection Size`: similar to count of zero before, will play all media items from the collection before continuing to the next schedule item
  - `Playlist Item Size`: will play all media items from the current playlist item before continuing to the next schedule item
  - `Multi-Episode Group Size`: will play all media items from the current multi-part episode group, or one ungrouped media item
- Change watermark width and margins to allow decimals
- Move `Add To Collection` button to overflow menu on all media cards, and add `Show Media Info` to overflow menu
  - This allows showing media info for all media kinds
- Unify on a multi-platform base docker tag (`latest` and `develop`)
  - `amd64`, `arm64`, `arm/v7` platforms are now all supported in the base docker tag
  - Other docker platform tags are deprecated and will receive no new updates after the next release
  - A health check has been added to notify users (on `-arm` or `-arm64` tags) of this change

### Fixed
- Fix QSV acceleration in docker with older Intel devices
- Fix HDR transcoding with NVIDIA accel for:
    - All NVIDIA docker users
    - Windows NVIDIA users who have set the `ETV_DISABLE_VULKAN` env var
- Fix audio sync issue with QSV acceleration
- YAML playout: fix history for marathon and playlist content
  - This allows playouts to be extended correctly, instead of always resetting to the earliest item in each group
- Fix using channel External Logo URL as watermark
- Fix display of SVG channel logo and watermark in admin UI
  - Existing SVG logos and watermarks will have to be re-uploaded to display properly in the admin UI
  - This does not affect streaming at all; existing artwork still works fine for streaming
- Classify HDHR endpoints as streaming endpoints
  - This allows these endpoints to be accessed through port `ETV_STREAMING_PORT` (default `8409`)
  - This only matters if you configured `ETV_UI_PORT` to be a different value, which makes UI endpoints inaccessible on the streaming port
- Update Plex movie/other video plot ("summary") during library deep scan
- Fix compatibility with ffmpeg 7.2+ when using NVIDIA accel and 10-bit source content
- Fix some NVIDIA edge cases when media servers don't provide video bit depth information
- Fix VAAPI tonemap failure
- Fix green bars after VAAPI tonemap
- Fix bug where playout mode `Multiple` would ignore fixed start time
- Fix block playout EPG generation to use `XMLTV Time Zone` setting
- Fix adding "official" Trakt lists
- Fix searching for `collection` names with spaces or other special characters, e.g. `collection:"Movies - Action"`
- Fix QSV transcoding errors when scaling
- Fix QSV frame freezing in browser
- Fix some stream continuity issues, and some cases where audio sync is lost at transition
- Fix HDR transcoding with AMD VAAPI accel
- Allow paths longer than 255 characters in MySql databases

## [25.2.0] - 2025-06-24
### Added
- Add `linux-musl-x64` artifact for users running Alpine x64
- Add API endpoint to empty trash (POST to `/api/maintenance/empty_trash`)
  - e.g. `curl -XPOST -d '' http://localhost:8409/api/maintenance/empty_trash`
- Add remote IP and user agent to HTTP request logging
- Add environment variables to allow ETV to run UI and streaming on separate ports
    - `ETV_STREAMING_PORT`: port used for streaming requests, defaults to 8409
    - `ETV_UI_PORT`: port used for admin UI, defaults to 8409
- Publish docker images to ghcr.io (`ghcr.io/ersatztv/ersatztv`)
- Add new option `Fixed Start Time Behavior` to Schedules and Schedule Items
  - Schedules can set a default behavior for all items
  - Schedule items can override this default behavior
  - Possible values are:
    - `Strict`: Always wait for the exact start time, even if that means waiting (adding unscheduled time) until the next day
    - `Flexible`: Start scheduling immediately (do not wait) if waiting (adding unscheduled time) would go into the next day
  - As an example, if the current scheduling time is 6:02 AM and the next schedule item has a fixed start time of 6:00 AM
    - `Strict` will add nearly 24h (23:58) of unscheduled time so that it can start exactly at 6:00 AM the next day
    - `Flexible` will NOT add unscheduled time, and will schedule its item at 6:02 AM (which may also affect the scheduling of later items)
- Add basic HDR transcoding support
  - VAAPI may use hardware-accelerated tone mapping (when opencl accel is also available)
  - NVIDIA may use hardware-accelerated tone mapping (when vulkan accel and libplacebo filter are also available)
  - QSV may use hardware-accelerated tone mapping (when hardware decoding is used)
  - In all other cases, HDR content will use a software pipeline
  - The tonemap algorithm can be configured in the ffmpeg profile
- Use hardware-accelerated padding with VAAPI
- Add environment variable `ETV_DISABLE_VULKAN`
  - Any non-empty value will disable use of Vulkan acceleration and force software tonemapping
  - This may be needed with misbehaving NVIDIA drivers on Windows
- Add health check error when invalid VAAPI device and VAAPI driver combination is used in an active ffmpeg profile
  - This makes it obvious when hardware acceleration will not work as configured
- Add button in schedule editor to clone schedule item
- Allow YAML playout sequence definitions to reference other sequences
  - Cycles will be detected and logged, and sequences with cycles will prevent the playout from building
- Add `repeat` property to YAML sequence instruction
  - This tells the playout builder how many times this sequence should repeat
  - Omitting this value is the same as setting it to `1`
- Add `collection` (name) to search index for manual collections created within ETV
  - Collections synchronized from media servers are still indexed as `tag`
- Allow searching by `smart_collection` (name)
  - Quotes are *always* required around each collection name when using this feature
    - e.g. `smart_collection:"one" OR smart_collection:"two"`
  - Cycles will be detected and logged, and searches with cycles will not work as expected
- Add all `ETV_*` environment variables to Troubleshooting > General info
- Add `External Logo URL` field to channel editor
  - Using external (public) logos should fix channel logo display for clients that don't proxy artwork (such as Plex)
  - Users who have customized the XMLTV channel template `channel.sbntxt` will need to update their templates again
    - This is because the templates require different logic for external URLs vs ETV-hosted URLs

### Changed
- Start to make UI minimally responsive (functional on smaller screens)
- Change how ETV determines which address to use for Plex connections
  - The active Plex connection (address) will only be cached for 30 seconds
  - When the connection is no longer cached, a ping will be sent to the last used address for Plex (the last address that had a successful ping)
  - If the ping is successful, the address will be cached for another 30 seconds
  - If the ping is not successful, all addresses will be checked again, and the first address to return a successful ping will be cached for 30 seconds
- Remove requirement to have Jellyfin admin user; user id is no longer required on requests to latest Jellyfin server
- Upgrade bundled ffmpeg on Windows from 6.1 to 7.1.1
- Upgrade VAAPI docker image Ubuntu base from 22 to 24; bundled ffmpeg from 6.1 to 7.1.1
- Upgrade NVIDIA docker image Ubuntu base from 20 to 24; bundled ffmpeg from 6.1 to 7.1.1
- Upgrade base, arm, arm64 docker images bundled ffmpeg from 6.1 to 7.1.1
- Unify all hardware acceleration methods in base docker images (`latest` and `develop`)
  - VAAPI, QSV and NVIDIA are now all supported in the base docker image
  - Other docker image tags are deprecated and will receive no new updates after the next release
  - A health check has been added to notify users (on `-vaapi` or `-nvidia` tags) of this change
- Schedule items editor: show currently selected row using background color instead of font weight

### Fixed
- Fix error message about synchronizing Plex collections from a Plex server that has zero collections
- Fix navigation after form submission when using `ETV_BASE_URL` environment variable
- Fix UI crashes when channel numbers contain a period `.` in locales that have a different decimal separator (e.g. `,`)
- Fix playout detail table to only reload once when resetting a playout
- Fix date formatting in playout detail table on reload (will now respect browser's `Accept-Language` header)
- Use cache busting to avoid UI errors after upgrading the MudBlazor library
- Fix multi-variant playlist to report more accurate `BANDWIDTH` value based on ffmpeg profile
- Fix detecting NVIDIA capabilities on Blackwell GPUs
- Fix decoder selection in NVIDIA pipeline
- Prevent playback order `Shuffle In Order` from being used with `Fill With Group Mode` as they are incompatible
- Fix XMLTV items not grouping properly (guide mode: `Filler`) due to post-roll filler

## [25.1.0] - 2025-01-10
### Added
- Add `Reset All Playouts` button to top of playouts page
- Add `rewind_on_reset` option to `wait_until` YAML playout instruction
  - This option allows YAML playouts to start in the past
- Add `advance` option to `epg_group` YAML playout instruction
  - When set to `false`, this option will lock the guide group without starting a new guide group
  - This can be helpful for "post roll" items that should be part of the previous item's guide group
- Add `Song Video Mode` to channel settings
  - `Default` - existing behavior
  - `With Progress` - show animated progress bar at bottom of generated video
    - Thanks to @JeckDev for the idea and the artwork
- Add fallback album art image for songs that have no album art
- Add `Vaapi Display` option to FFmpeg Profile
  - Possible values will be install-specific and sourced from `vainfo`
  - `drm` was the previous default value, and should be used in most cases
- Test all `Vaapi Display` values in `Troubleshooting` > `VAAPI Capabilities`
- Add `tag_full` field to search index
  - This field contains the same values as the existing `tag` field, but it is not analyzed or tokenized

### Changed
- **BREAKING CHANGE**: Change channel identifiers used in XMLTV to work around bad behavior in Plex

### Fixed
- Fix startup error with MySql backend caused by database cleaner
- Fix emptying trash with ElasticSearch backend
- Fix double loading of trash UI elements, and fix reloading of all UI elements after emptying trash
- Fix destroying channel preview player when preview dialog is closed
  - This bug made it difficult to "stop" a channel after previewing it
- Fix bug where deco default filler would never use hardware acceleration
- Fix deleting local libraries with MySql backend
- Fix `Scaling Behavior` `Crop` when content is smaller than FFmpeg Profile resolution
  - Now, content will properly scale beyond the desired resolution before cropping
- Fix displaying playout item durations that are greater than 24 hours
- Fix building playouts when playlist has been changed to have fewer items
- Fix selecting audio stream with preferred title
- Fix synchronizing Plex collections
  - If this breaks collection sync for you, you will need to update your Plex server
- Fix guide group generation for `duration` YAML instructions
- Fix default song background when targeting 4:3 resolutions
  - Previously the background was always 16:9 and was padded, now it will fill 4:3

## [0.8.8-beta] - 2024-09-19
### Added
- Add support for Plex Other Video libraries
  - These libraries will now appear as ETV Other Video libraries
  - Items in these libraries will have tag metadata added from folders just like local Other Video libraries
  - Thanks @raknam for adding this feature!
- Add *experimental* support for `On Demand` channel progress
  - With `On Demand` channel progress, the playout will only advance when the channel is being streamed
  - When the channel is idle, the playout is unmodified and will be shifted forward as needed so no content is missed
  - Setting a channel to `On Demand` progress will disable alternate schedules
  - The `On Demand` setting will only be used for `Flood` playouts (NOT `Block` or `External JSON`)
  - It is NOT recommended to use fixed start times with `On Demand` progress
    - This will probably be disabled with a future update
- Add `Default Filler` to `Deco` system
  - After all blocks are scheduled/added to the playout, a second pass will be made to insert filler
  - Default filler will be shuffled and inserted in all unscheduled time between blocks
  - Default filler will stop scheduling when the next item would extend into primary content
  - Alternatively, default filler can be configured to `Trim To Fit`
    - In this case, the last item that would extend into primary content is trimmed to end exactly when the primary content starts
- Add **experimental** playout type `YAML`
  - This playout type uses a YAML file to declare content and describe how the playout should be built
  - Content currently supports search queries
  - Playout instructions currently include `count`, `pad to next`, and `repeat`
    - `count`: add the specified number of items (from the referenced content) to the playout
    - `duration`: play the referenced content for the specified duration
    - `pad to next`: add items from the referenced content until the wall clock is a multiple of the specified minutes value
    - `repeat`: continue building the playout from the first instruction in the YAML file
- Add channel logo generation by @raknam
  - Channels without custom uploaded logos will automatically generate a logo that includes the channel name
- Add two new API endpoints
  - Reset playout for channel
    - POST `/api/channels/{channelNumber}/playout/reset`
  - Scan library
    - POST `/api/libraries/{libraryId}/scan`
- Add Deco setting to `Use Watermark During Filler`
  - This setting is turned OFF by default, meaning filler will NOT use the configured watermark unless this is manually turned on
- Add `Random Count` filler mode by @embolon
  - This mode will randomly schedule between zero and the provided count number of items
  - e.g. random count 3 will schedule between 0 and 3 filler items
- Add `Random Rotation` playback order for block scheduling by @embolon
  - This playback order will pick a random item from a randomly selected group (show or artist)
  - It is somewhat similar to the `Fill With Group` mode used in flood scheduling

### Fixed
- Add basic cache busting to XMLTV image URLs
  - This should help with clients not showing correct channel logos or posters
- Fix artwork in other video libraries by @raknam
- Fix adding items to empty playlists
- Fix filler preset editor and deco dead air fallback editor to only show supported collection types
- Fix infinite loop caused by impossible schedule (all collection items longer than schedule item duration)
- Fix selecting audio and subtitle streams with two-letter language codes
- Fix adding pad filler to content that is less than one minute in duration
- Generate unique identifier for virtual HDHomeRun tuner by @raknam
  - This allows a single Plex server to connect to multiple ETV instances
- Include *all* language codes from media library in preferred audio and subtitle language options
  - Language codes where an English name cannot be found will be at the bottom of the list
- Fix local libraries to detect external subtitle files with unrecognized language codes
- Fix playback selection of subtitles with unrecognized language codes
- Fix incorrectly removing block items that are hidden from EPG when deco filler is applied
- Fix deco selection when deco is scheduled until midnight
  - Previously, this deco item would be ignored so watermark and filler would be missing
- Fix movies with missing medata by generating fallback metadata
  - This allows these movies to appear in the Trash where they can be deleted
- Fix synchronizing trakt lists from users with special characters in their username
  - Note that these lists MUST be added as URLs; the short-form `user/list` will NOT work with special characters
- Fix local subtitle scanner to detect non-lowercase extensions (e.g. `Movie (2000).EN.SRT`)
- Fix adding a single image to a manual collection from search results
- Fix loading manual collection view when collection contains images
- Fix edge case where block playout history would get stuck and repeat an item
- Fix adjusting watermark opacity when watermark already contains alpha channel (is already transparent)

### Changed
- Remove some unnecessary API calls related to media server scanning and paging
- Improve trakt list URL validation; non-trakt URLs will no longer be requested
- Prevent saving block templates when blocks are overlapping
  - This can happen if block durations are changed for blocks that are already on the template
- Redirect variant playlist request to proper URL for starting `HLS Segmenter` session when no session is active
  - This can happen when some clients "pause" long enough for the session to stop in ETV
  - When the client resumes playback, it requests the temp playlist URL which is now invalid e.g. `/iptv/session/1/hls.m3u8` (not the original URL `/iptv/channel/1.m3u8`)
  - To fix, the client will be redirected back to the original URL in this case which will create a new session

## [0.8.7-beta] - 2024-06-26
### Added
- Add `Active Date Range` to block playout template editor to allow limiting templates to a specific date range
  - This is year-agnostic, meaning the Month/Day range will apply to every year
  - This also supports wrapping the end of the year (e.g., start 12/1 and end 1/15)
- Add new `Deco` system for "decorating" channels with non-primary content
  - Decos currently contain
    - Watermarks
    - Dead Air Fallback (i.e. fallback filler)
  - Similar to blocks, decos have deco groups for organization
  - Similar to blocks, decos have deco templates for filling a "day" with decos
  - In the playout template editor, playout template items can have *both* a block template and a deco template
    - This allows watermarks and dead air fallback to change at different times than primary content
  - Block playouts can also have a default deco
    - This will apply whenever a deco template is missing, or when a deco template item cannot be found for the current time
    - Effectively, this sets a default watermark and dead air fallback for the entire playout
- Add `XMLTV Days To Build` setting, which is distinct from the existing `Playout Days To Build` setting
  - The value for `XMLTV Days To Build` cannot be larger than `Playout Days To Build`
  - This allows, for example, a week of playout data while optimizing XMLTV data to only a day or two
- Add health check to detect config folder issue on MacOS
  - ETV versions through v0.8.4-beta (using dotnet 7) stored config data in `$HOME/.local/share/ersatztv`
  - ETV versions starting with v0.8.5-beta (using dotnet 8) store config data in `$HOME/Library/Application Support/ersatztv`
  - If a dotnet 8 version of ETV has NOT been launched on MacOS, it will automatically migrate the config folder on startup
  - If a dotnet 8 version of ETV *has* been launched on MacOS, a failing health check will display with instructions on how to resolve the config issue to restore data
- Add `Video Profile` setting to `FFmpeg Profile` editor when `h264` format is selected
- Add `Video Preset` setting to `FFmpeg Profile` editor for some combinations of acceleration and video format:
  - `Nvenc` / `h264`
  - `Nvenc` / `hevc`
  - `Qsv` / `h264`
  - `Qsv` / `hevc`
  - `None` / `h264`
  - `None` / `hevc`
- Add *experimental* list type `Playlist`
  - Playlists contain an ordered list of:
    - Collections
    - Multi-Collections
    - Smart Collections
    - TV Shows
    - TV Seasons
    - Artists
    - Movies
    - Episodes
    - Music Videos
    - Other Videos
    - Songs
    - Images
  - Playlists can be added to schedules as a schedule item
  - Each time through the playlist, one item will be scheduled from each playlist item (if `Play All` is unchecked)
    - NB: This does not mean every collection will always schedule one item; the normal flood playout restrictions like duration and fixed start times still apply here
  - If `Play All` is checked, that playlist item will play all of its items each time through the playlist
    - This can be helpful if you want to play entire collections in a specific order, e.g.
      - Every episode from Show 1 Season 2
      - Every episode from Show 2 Season 3
      - Every episode from Show 1 Season 3
  - Playlist items with fewer media items will be re-shuffled (if applicable) before those with more media items
- Add two new environment variables to customize config and transcode folder locations
  - `ETV_CONFIG_FOLDER`
  - `ETV_TRANSCODE_FOLDER`
- Add checkbox to allow use of B-frames in FFmpeg Profile (disabled by default)

### Fixed
- Fix some cases of 404s from Plex when files were replaced and scanning the library from ETV didn't help
- Fix more wildcard search phrase queries (when wildcards are used in quotes, like `title:"law & order*"`)
- Fix non-wildcard simple queries when asterisks are used in quotes, like `title:"m*a*s*h"`
- Fix bug where channels would unnecessarily wait on each other
  - e.g. in-progress streams would delay responding with a playlist when new streams were starting
- Update Plex show title in ETV when changed in Plex
- Reindex seasons and episodes when show is updated from media server
  - This is needed to keep `show_*` tags accurate in the search index (e.g., `show_title`, `show_studio`)
- Fix external subtitle detection to support forced/sdh subtitles with language tag before and after forced/sdh tag:
  - `Something.forced.en.srt`
  - `Something.sdh.en.srt`
  - `Something.en.forced.srt`
  - `Something.en.sdh.srt`
- Fix playback from Jellyfin 10.9 by allowing playlist HTTP HEAD requests
- Fix `HLS Segmenter V2` segment duration (previously 10s, now 4s)
- Fix `HLS Segmenter V2` error video generation
- Fix MySql database migrations
- Fix Plex library scans with MySql/MariaDB
- Fix block playout playback when no deco is configured
- Fix `HLS Segmenter V2` to delete old segments (use less disk space while channel is active)
- Fix template and deco template editors to prevent items that go beyond midnight
- Fix block playout random seeds
  - Different blocks within a single playout will now correctly use different random seeds (shuffles)
  - Erasing block playout history will also generate new random seeds for the playout
- Fix building playouts that use mid-roll filler and have content without chapter markers
  - When this happens, mid-roll will be treated as post-roll
- Fix VAAPI decoder capability check
  - This caused some streams to incorrectly use software decoding
- Fix scheduling loop/failure caused by some duration schedule items
- Fix `video_bit_depth` search field for Plex media
- Fix template and deco template editors with MariaDB/MySql backend
- Fix transcoding 10-bit source content using QSV acceleration on Windows

### Changed
- Show health checks at top of home page; scroll release notes if needed
- Improve `HLS Segmenter V2` compliance by:
  - Serving fmp4 segments when `hevc` video format is selected
    - > 1.5. The container format for HEVC video MUST be fMP4.
  - Using accurate BANDWIDTH value in multi-variant playlist
  - Using proper MIME types for statically-served `.m3u8` and `.ts` files
  - Serving playlists with gzip compression
- Use `HLS Segmenter V2` for channel preview when channel is configured for `HLS Segmenter V2`
- Detect and use `/dev/dri/card*` devices in addition to `/dev/dri/render*` devices
- Change default folder locations in docker using new environment variables
    - `ETV_CONFIG_FOLDER` - now defaults to `/config`
    - `ETV_TRANSCODE_FOLDER` - now defaults to `/transcode`
    - If the old locations are still present in docker, these variables will be ignored, so you can migrate at your own pace
      - Old config location: `/root/.local/share/ersatztv`
      - Old transcode location: `/root/.local/share/etv-transcode`

## [0.8.6-beta] - 2024-04-03
### Added
- Add `show_studio` and `show_content_rating` to search index for seasons and episodes
- Add two new global subtitle settings:
  - `Use embedded subtitles`
    - Default value: `true`
    - When disabled, embedded subtitles will not be considered for extraction (text subtitles), or playback (all embedded subtitles)
  - `Extract and use embedded (text) subtitles`
    - Default value: `false`
    - When enabled, embedded text subtitles will be periodically extracted, and considered for playback
- Add `sub_language` and `sub_language_tag` fields to search index
- Add `/iptv` request logging in its own log category at debug level
- Add channel guide (XMLTV) template system
  - Templates should be copied from `_channel.sbntxt`, `_movie.sbntxt`, `_episode.sbntxt`, `_musicVideo.sbntxt`, `_song.sbntxt`, or `_otherVideo.sbntxt` which are located in the config subfolder `templates/channel-guide`
    - Copy the file, remove the leading underscore from the name, and only make edits to the copied file
  - The default templates will be extracted and overwritten every time ErsatzTV is started
  - The templates use [scribian](https://github.com/scriban/scriban/tree/master/doc) template syntax
  - The templates contain comments describing which fields are available for use in the templates
- Add *experimental* and *incomplete* `Images` library kind
  - Image libraries have fallback metadata added like Other Video libraries (every folder is a tag)
  - Image library items currently default to a duration of 15 seconds
    - The `Media` > `Images` page can be used to configure image durations at a folder level
    - Child folders with unset durations will inherit the closest ancestor's duration
- Add *experimental* new streaming mode `HLS Segmenter V2`
  - In my initial testing, this streaming mode produces significantly fewer playback warnings/errors
  - If it tests well for others, it *may* replace the current `HLS Segmenter` in a future release
- Add setting to change XMLTV data from `Local` time zone to `UTC`
  - This is needed because some clients (incorrectly) ignore time zone specifier and require UTC times
- Support `.ogv` video files in local libraries

### Fixed
- Fix antiforgery error caused by reusing existing browser tabs across docker container restarts
  - Data protection keys will now be persisted under ErsatzTV's config folder instead of being recreated at startup
- Fix bug updating/replacing Jellyfin movies
  - A deep scan can be used to fix all movies, otherwise any future updates made to JF movies will correctly sync to ETV
- Automatically generate JWT tokens to allow channel previews of protected streams
- Fix bug applying music video fallback metadata
- Fix playback of media items with no audio streams
- Fix timestamp continuity in `HLS Segmenter` sessions
  - This should make *some* clients happier
- Fix `Other Video`, `Song` and `Image` fallback metadata tags to always include parent folder (folder added to library)
- Allow playback of items with any positive duration, including less than one second
- Fix VAAPI transcoding of OTA content containing A53 CC data
- Fix AV1 software decoder priority (`libdav1d`, `libaom-av1`, `av1`)
- Fix some stream failures caused by loudnorm filter
- Fix multi-collection editor improperly disabling collections/smart collections that haven't already been added to the multi-collection
- Fix path replacement logic when media server paths use inconsistent casing (e.g. `\\SERVERNAME` AND `\\ServerName`)
- Fix *many* search queries, including actors with the name `Will`
- Fix sqlite `database is locked` error that would crash ETV on startup after search index corruption
- Fix bug where replacing files in Plex would be missed by subsequent ETV library scans
  - This fix will require a one-time re-scan of each Plex library in full
  - After the initial full scan, incremental scans will behave as normal
- Fix edge case where some local episodes, music videos, other videos, songs, images would not automatically be restored from trash
- Fix `MPEG-TS` playback when JWT tokens are enabled for streaming endpoints

### Changed
- Log search index updates under scanner category at debug level, to indicate a potential cause for the UI being out of date
- Batch search index updates to keep pace with library scans
  - Previously, search index updates would slowly process over minutes/hours after library scans completed
  - Search index updates should now complete at the same time as library scans
- Do not unnecessarily update the search index during media server library scans
- Use different library for reading song metadata that supports multiple tag entries
- Update `/iptv` routing to make UI completely inaccessible from that path prefix
- Use CUDA 11 instead of CUDA 12 in NVIDIA docker image to significantly lower required driver version
- Allow block durations with 5-minute increments (e.g., 5 min, 10 min, 15 min, etc.)

## [0.8.5-beta] - 2024-01-30
### Added
- Respect browser's `Accept-Language` header for date time display
- Add new schedule item setting `Fill With Group Mode`
  - This setting is only available when a `Collection`, `Multi-Collection` or `Smart Collection` is scheduled with `Duration` or `Multiple` playout modes
  - Use this setting when you want to schedule a collection containing groups (show or artists), with only videos from a single group (show or artist) being used in each rotation
  - The options are `None`, `Ordered Groups` and `Shuffled Groups`:
    - `None`: no change to scheduling behavior - all groups (shows and artists) will be shuffled/ordered together
    - `Ordered Groups`: each time this item is scheduled, the entire `Duration` or `Multiple` will be filled with a single group, and the groups will rotate in a fixed order
    - `Shuffled Groups`: each time this item is scheduled, the entire `Duration` or `Multiple` will be filled with a single group, and the groups will rotate in a shuffled order
- Add new playout type `External Json`
  - Use this playout type when you want to manage the channel schedule using DizqueTV
  - You must point ErsatzTV to the channel number json file from DizqueTV, e.g. `channels/1.json`
  - For playback, ErsatzTV will first check for the appropriate media file file locally
    - If found, ErsatzTV will run ffprobe to get statistics immediately before streaming from disk
  - When local files are unavailable, ErsatzTV must be logged into the same Plex server as DizqueTV
    - ErsatzTV will ask Plex for statistics immediately before streaming from Plex
- Add new *experimental* playout type `Block`
  - **This playout type is under active development and updates may reset or delete related playout data**
  - Many planned features are missing, incomplete, or result in errors. This is expected.
  - Block playouts consist of:
    - `Blocks` - ordered list of items to play within the specified duration
    - `Templates` - a generic "day" that consists of blocks scheduled at specific times
    - `Playout Templates` - templates to schedule using the specified criteria. Only one template will be selected each day
  - Much more to come on this feature as development continues
- Show chapter markers in movie and episode media info
- Add two new API endpoints for interacting with transcoding sessions (MPEG-TS and HLS Segmenter):
  - GET `/api/sessions`
    - Show brief info about all active sessions
  - DELETE `/api/session/{channel-number}`
    - Stop the session for the given channel number
- Add channel preview (web-based video player)
  - Channels MUST use `H264` video format and `AAC` audio format
  - Channels MUST use `MPEG-TS` or `HLS Segmenter` streaming modes
    - Since `MPEG-TS` uses `HLS Segmenter` under the hood, the preview player will use `HLS Segmenter`, so it's not 100% equivalent, but it should be representative
- Add button to stop transcoding session for each channel that has an active session
- Add more log levels to `Settings` page, allowing more specific debug logging as needed
    - Default Minimum Log Level (applies when no other categories/level overrides match)
    - Scanning Minimum Log Level
    - Scheduling Minimum Log Level
    - Streaming Minimum Log Level

### Fixed
- Fix error loading path replacements when using MySql
- Fix tray icon shortcut to open logs folder on Windows
- Unlock playout when playout build fails
- Ignore errors deleting old HLS segments; this should improve stream reliability
- Update show year when changed within Plex
- Fix crop scale behavior with NVIDIA, QSV acceleration
- Fix bug that corrupted uploaded images (watermarks, channel logos)
  - Re-uploading images should fix them
- Recreate XMLTV channel list (including logos) when channels are edited in ErsatzTV
  - This bug caused the ErsatzTV logo to be used instead of channel logos in some cases
- Update drop down search results in main search bar when items are created/edited/removed
- Fix green line at bottom of video when NVIDIA accel is used with intermittent watermark
- Fix error starting streaming session when subtitles are still being extracted for the current item

### Changed
- Upgrade from .NET 7 to .NET 8
- In schedule items, disambiguate seasons from shows with the same title by including show year
  - Old format: `Show Title (Season Number)`
  - New format: `Show Title (Show Year) - Season Number`
- Remove FFmpeg Profile `Normalize Loudness` option `dynaudnorm` as it often caused streams to fail to start
- Disable loudness normalization by default in new FFmpeg Profiles
- Use AAC audio format by default in new FFmpeg Profiles

## [0.8.4-beta] - 2023-12-02
### Fixed
- Fix playout builder crash with improperly configured pad filler preset
- Properly validate filler preset mode pad to require `filler pad to nearest minute` value
- Fix bug where previously-synchronized collection tags would disappear
  - This bug affected Jellyfin, Emby and Plex collections
- Fix detection of AMF hardware acceleration on Windows

## [0.8.3-beta] - 2023-11-22
### Added
- Add `Scaling Behavior` option to FFmpeg Profile
  - `Scale and Pad`: the default behavior and will maintain aspect ratio of all content
  - `Stretch`: a new mode that will NOT maintain aspect ratio when normalizing source content to the desired resolution
  - `Crop`: a new mode that will scale beyond the desired resolution (maintaining aspect ratio), and crop to desired resolution
    - **This mode does NOT detect black and intelligently crop**
    - The goal is to fill the canvas by over-scaling and cropping, instead of minimally scaling and padding
- Include `inputstream.ffmpegdirect` properties in channels.m3u when requested by Kodi
- Log playout item title and path when starting a stream
  - This will help with media server libraries where the URL passed to ffmpeg doesn't indicate which file is streaming
- Add QSV Capabilities to Troubleshooting page
- Add `language_tag` and `seconds` fields to search index
- Allow synchronizing Plex `TV Show` libraries that use `Personal Media Shows` agent
- Include Noto CJK Fonts in docker images to support those characters in generated subtitles like songs and music video credits
- Support show fallback metadata with folder names like `Show.Name(1992)`

### Fixed
- Fix playout bug that caused some schedule items with fixed start times to be pushed to the next day
- Fix playout bug that prevented padded durations from fitting within a schedule item of the same duration
  - For example, filler that padded to 30 minutes would often not fit in a 30 minute duration schedule item
- Fix VAAPI transcoding 8-bit source content to 10-bit
- Fix NVIDIA subtitle scaling when `scale_npp` filter is unavailable
- Remove ffmpeg and ffprobe as required dependencies for scanning media server libraries
  - Note that ffmpeg is still *always* required for playback to work
- Fix PGS subtitle pixel format with Intel VAAPI
- Fix some cases where `Copy` button would fail to copy to clipboard
- Fix some cases where ffmpeg process would remain running after properly closing ErsatzTV
- Fix QSV HLS segment duration
  - This behavior caused extremely slow QSV stream starts
- Fix displaying multiple languages in UI for movies, artists, shows
- Fix MySQL queries that could fail during media server library scans
- Fix scanning Jellyfin libraries when library options and/or path infos are not returned from Jellyfin API
- Fix error indexing music videos in `File Not Found` state
- Fix bug scheduling duration filler when filler collection contains item with zero duration
- Fix bug displaying television seasons for shows that have no year metadata

### Changed
- Upgrade ffmpeg to 6.1, which is now *required* for all installs
- Use new ffmpeg throttling method to minimize cpu/gpu use without impacting audio normalization
- Change FFmpeg Profile `Normalize Loudness` setting from checkbox to dropdown
  - `Off`: do not normalize loudness
  - `loudnorm`: use `loudnorm` filter to normalize loudness (generally higher CPU use)
  - `dynaudnorm`: use `dynaudnorm` filter to normalize loudness (generally lower CPU use)
- Jellyfin collection scanning will no longer happen after every (automatic or forced) library scan
    - Automatic/periodic scans will check collections one time after all libraries have been scanned
    - There is a new table in the `Media` > `Libraries` page with a button to manually re-scan Jellyfin collections as needed
- In FFmpeg Profile editor, only display hardware acceleration kinds that are supported by the configured ffmpeg
- Test QSV acceleration if configured, and fallback to software mode if test fails
- Detect QSV capabilities on Linux (supported decoders, encoders)
- Use hardware acceleration for error messages/offline messages
- Try to parse season number from season folder when Jellyfin does not provide season number
  - This *may* fix issues where Jellyfin libraries show all season numbers as 0 (specials)
- Rework Plex collection scanning
    - Automatic/periodic scans will check collections one time after all libraries have been scanned
    - There is a table in the `Media` > `Libraries` page with a button to manually re-scan Plex collections as needed
    - Plex smart collections will now be synchronized as tags, similar to other Plex collections

## [0.8.2-beta] - 2023-09-14
### Added
- Automatically rebuild search index after improper shutdown
- Add *experimental* support for Elasticsearch as search index backend
  - No query changes should be needed since ES is backed by lucene and supports the same query syntax
  - This can be configured using the following env vars (note the double underscore separator `__`)
    - `ELASTICSEARCH__URI` (e.g. `http://localhost:9200`)
    - `ELASTICSEARCH__INDEXNAME` (default is `ersatztv`)
- Add *experimental* support for MySQL/MariaDB database provider
  - ***There is no functionality to migrate data between providers***
  - This can be configured using the following env vars (note the double underscore separator `__`)
    - `PROVIDER` - set to `MySql`
    - `MYSQL__CONNECTIONSTRING` - (e.g. `Server=localhost;Database=ErsatzTV;Uid=root;Pwd=ersatztv;`)
- Add option to use shared Plex servers, not just owned servers
  - This can be enabled by setting the env var `ETV_ALLOW_SHARED_PLEX_SERVERS` to any non-empty value
- Show Plex server names in Libraries page

### Fixed
- Fix subtitle scaling when using QSV hardware acceleration
- Fix log viewer crash when log file contains invalid data
- Clean channel guide cache on startup (delete channels that no longer exist)
- Fix Emby movie libraries so local file access is not required
- Fix adding alternate schedule
- Fix parsing show title from NFO file that also contains season information

### Changed
- Optimize transcoding session to only work ahead (at max speed) for 3 minutes before throttling to realtime
  - This should *greatly* reduce cpu/gpu use when joining a channel, particularly with long content
- Allow manually editing (typing) schedule item fixed start time
- Use different control for editing schedule item duration, and allow 24-hour duration
  - This is needed if you want a default/fallback alternate schedule to fill the entire day with one schedule item
  - The schedule item should have a fixed start time of midnight (00:00) and a duration of 24 hours
- Use Direct3D 11 for QSV acceleration on Windows

## [0.8.1-beta] - 2023-08-07
### Added
- Add custom resolution management to `Settings` page

### Fixed
- Only allow a single instance of ErsatzTV to run
  - This fixes some cases where the search index would become unusable
- Fix VAAPI rate control mode capability check

### Changed
- Rework startup process to show UI as early as possible
  - A minimal UI will indicate when the database and search index are initializing
  - The UI will automatically refresh when the initialization processes have completed
- Force ffmpeg to use one thread when hardware acceleration is used since hardware acceleration does not support multiple threads

## [0.8.0-beta] - 2023-06-23
### Added
- Disable playout buttons and show spinning indicator when a playout is being modified (built/extended, or subtitles are being extracted)
- Automatically reload playout details table when playout build is complete
- Add `Discard To Fill Attempts` setting to duration playout mode
  - This setting only has an effect when it's configured to be greater than zero and when using `Shuffle` or `Random` playback order
  - When the current item is longer than the remaining duration, it will be discarded and ETV will try to fit the next item in the collection, up to the configured number of times
  - When the remaining duration is shorter than all items in the collection, the normal filler logic will be used
- Add `Finish` column to playout detail table

### Fixed
- Skip checking for subtitles to extract when subtitles are not enabled on a channel/schedule item
- Properly scale subtitles when using hardware acceleration
- Fix color normalization of content with missing color metadata when using NVIDIA acceleration
- `VAAPI`: explicitly use `CQP` rate control mode when it's the only compatible mode
- Fix scaling anamorphic Emby content that Emby claims is not anamorphic

### Changed
- `HLS Direct` streaming mode
    - Use `MPEG-TS` container/output format by default to maintain v0.7.8 compatibility
      - `MP4` and `MKV` container/output format can still be configured in `Settings`
    - Improve `MP4` compatibility with certain content
- For `Pad` and `Duration` filler - prioritize filling the configured pad/duration
  - This will skip filler that is too long in an attempt to avoid unscheduled time
  - You may see the same filler more often, which means you may want to add more filler to your library so ETV has more options
- Update ffmpeg, libraries and drivers in all docker images

## [0.7.9-beta] - 2023-06-10
### Added
- Synchronize actor metadata from Jellyfin and Emby television libraries
  - New libraries and new episodes will get actor data automatically
  - Existing libraries can deep scan (one time) to retrieve actor data for existing episodes
- `HLS Direct` streaming mode
    - Use `MP4` container/output format by default, with new global option to use `MKV` container/output format
    - `MP4` output format: stream copy dvd subtitles
    - `MKV` output format: stream copy any embedded subtitles

### Fixed
- Fix extracting embedded text subtitles that had been incompletely extracted in the past
- Fix fallback filler looping by forcing software mode for this content
  - Other content will still use hardware acceleration as configured
  - Hardware-accelerated fallback filler may be re-enabled in the future
- Fix playout building when shuffle in order is used with a single media item
- Fix pgs subtitle burn in from media server libraries
- Fix subtitle and watermark overlays with RadeonSI VAAPI driver
- Fix NVIDIA pipeline to use hardware-accelerated decoder with 8-bit h264 content

### Changed
- Timeout playout builds after 2 minutes; this should prevent playout bugs from blocking other functionality

## [0.7.8-beta] - 2023-04-29
### Added
- Add `Season, Episode` playback order
  - This is currently *only* available when a show is added directly to a schedule
  - This will ignore release date and sort exclusively by season number and then by episode number
- Add `Show Media Info` button to movie and episode detail pages for troubleshooting

### Fixed
- Limit `HLS Direct` streams to realtime speed
- Fix `Reset Playout` button to use worker thread instead of UI thread
  - This fixes potential UI hangs and database concurrency bugs
- Maintain watermark alpha channel (built-in transparency) using QSV acceleration
- Properly extract and burn in embedded text subtitles using Jellyfin, Emby and Plex libraries
- Fix bug where deleting a channel would not remove its data from XMLTV
- Fix colorspace filter for some files with invalid color metadata
- Fix playback of external subtitles on Windows
- Fix vobsub subtitle burn in from media server libraries

### Changed
- Remove duplicate items from smart collections before scheduling
  - i.e. shows no longer need to be filtered out if search results also include episodes
  - Certain multi-collection scenarios may still include duplicates across multiple collections
- Use autocomplete fields for collection searching in schedule items editor
  - This greatly improves the editor performance
- Ignore dot underscore files

## [0.7.7-beta] - 2023-04-07
### Added
- Use `plot` field from Other Video NFO metadata as XMLTV description
- Add detailed warning log when a file is added to ErsatzTV more than once

### Fixed
- Fix updating (re-adding) Trakt lists to properly use new metadata ids that were not present when originally added
- Fix local show library scanning with non-english season folder names, e.g. `Staffel 02`
- Fix bug where local libraries would merge with media server libraries when the same file was added to both libraries
- Fix transcoding some 10-bit content from media servers using VAAPI acceleration
- Fix decoding of MPEG-4 Part 2 (e.g. DivX) content using NVIDIA acceleration
- Fix color normalization from `bt470bg` to `bt709` using QSV acceleration
- Fix adding files to search index with unknown video codec
- Fix subtitle burn-in (embedded or external) using Jellyfin, Emby and Plex libraries
    - **This requires a one-time full library scan, which may take a long time with large libraries.**

### Changed
- Use Poster artwork for XMLTV if available
  - If Poster artwork is unavailable, use Thumbnail
- Improve XMLTV response time by caching data as playouts are updated

## [0.7.6-beta] - 2023-03-24
### Added
- Add `Troubleshooting` page with aggregated settings/hardware accel info for easy reference
- Read `director` fields from music video NFO metadata
- Pass `directors` and `studios` to music video credit templates
- Add optional JSON Web Token (JWT) query string auth for streaming endpoints (everything under `/iptv`)
  - This can be configured using the following env var (note the double underscore separator `__`)
    - `JWT__ISSUERSIGNINGKEY`
  - When configured, a JWT signed with the configured signing key is required to be passed in the query string as `access_token`, for example:
    - `http://localhost:8409/iptv/channels.m3u?access_token=ABCDEF`
    - `http://localhost:8409/iptv/xmltv.xml?access_token=ABCDEF`
  - When channels are retrieved this way, the access token will automatically be passed through to all necessary urls
  - Note that ONLY the `/iptv` endpoints will require auth when JWT is configured

### Fixed
- Fix scaling anamorphic content from non-local libraries
- Fix direct streaming content from Jellyfin that has external subtitles
  - Note that these subtitles are not currently supported in ETV, but they did cause a playback issue
- Fix Jellyfin, Emby and Plex library scans that wouldn't work in certain timezones
- Fix song normalization to match FFmpeg Profile bit depth
- Fix bug playing some external subtitle files (e.g. with an apostrophe in the file name)
- Fix bug detecting VAAPI capabilities when no device is selected in active FFmpeg Profile
- Fix playout mode duration bugs in XMLTV
  - Tail mode filler will properly include filler duration in XMLTV
  - Duration that wraps across midnight will no longer have overlapping items in XMLTV
- Maintain collection progress across all alternate schedules on a playout
- Fix color normalization from `bt470bg` to `bt709`

### Changed
- Ignore case of video and audio file extensions in local folder scanner
  - For example, the scanner will now find `movie.MKV` as well as `movie.mkv` on case-sensitive filesystems
- Include multiple `display-name` entries in generated XMLTV
  - Plex should now display the channel number instead of the channel id (e.g. `1.2` instead of `1.2.etv`)
- Rework concurrency a bit
  - Playout builds are no longer blocked by library scans
  - Adding Trakt lists is no longer blocked by library scans
  - All library scans (local and media servers) run sequentially
- Emby collection scanning will no longer happen after every (automatic or forced) library scan
  - Automatic/periodic scans will check collections one time after all libraries have been scanned
  - There is a new table in the `Media` > `Libraries` page with a button to manually re-scan Emby collections as needed
- For performance reasons, limit console log output to errors on Windows
  - Other platforms are unchanged
  - Log file behavior is unchanged

## [0.7.5-beta] - 2023-03-05
### Added
- Use AV1 hardware-accelerated decoder with VAAPI, QSV, NVIDIA when available
- Use VP9 hardware-accelerated decoder with VAAPI when available

### Fixed
- Align default docker image (no acceleration) with new images from [ErsatzTV-ffmpeg](https://github.com/ErsatzTV/legacy-ffmpeg)
- Fix some transcoding pipelines that use software decoders
- Improve VAAPI encoder capability detection on newer hardware
- Fix trash page to properly display episodes with missing metadata or titles
- Fix playback of content with yuv444p10le pixel format
- Fix case where some multi-episode files from Plex would crash the scanner

### Changed
- Upgrade all docker images and windows builds to ffmpeg 6.0
- Plex, Jellyfin and Emby libraries now retrieve all metadata and statistics from the media server
  - File systems will no longer be periodically scanned for libraries using these media sources
- Plex, Jellyfin and Emby libraries now direct stream content when files are not found on ErsatzTV's file system
  - Content will still be normalized according to the Channel and FFmpeg Profile settings
  - Streaming from disk is preferred, so every playback attempt will first check the local file system
- Use libvpl instead of libmfx to provide intel acceleration in vaapi docker images
- Search queries no longer remove duplicate results as this was causing incorrect behavior
- Prioritize audio streams that are flagged as "default" over number of audio channels
    - For example, a video with a stereo commentary track and a mono "default" track will now prefer the "default" track
- Support many more season folder names with local television libraries

## [0.7.4-beta] - 2023-02-12
### Added
- Add button to copy/clone schedule from schedules table
- Synchronize episode tags and genres from Jellyfin, Emby and Local show libraries
- Add `Deep Scan` button to Jellyfin and Emby libraries
  - This is now required to update some metadata for existing libraries, when targeted updates are not possible
  - For example, if you already have tags and genres on your episodes in Jellyfin or Emby, you will need to deep scan each library to update that metadata on existing items in ErsatzTV

### Fixed
- Fix many QSV pipeline bugs
- Fix MPEG2 video format with QSV and VAAPI acceleration
- Fix playback of content with undefined colorspace
- Fix NVIDIA color normalization with VP9 sources
- Fix fallback filler looping
- Fix bug where some libraries would never scan
- Fix filler ordering so post-roll is properly scheduled after padded mid-roll
- Fix pre/post-roll filler padding when used with mid-roll
  - This caused overlapping schedule items, fallback filler that was too long, etc.

### Changed
- Merge generated `Other Video` folder tags with tags from sidecar NFO
- Prioritize audio streams that are flagged as "default" when multiple candidate streams are available
  - For example, a video with a stereo commentary track and a stereo "default" track will now prefer the "default" track

## [0.7.3-beta] - 2023-01-25
### Added
- Attempt to release memory periodically
- Add OpenID Connect (OIDC) support (e.g. Keycloak, Authelia, Auth0)
  - This only protects the management UI; all streaming endpoints will continue to allow anonymous access
  - This can be configured with the following env vars (note the double underscore separator `__`)
    - `OIDC__AUTHORITY`
    - `OIDC__CLIENTID`
    - `OIDC__CLIENTSECRET`
    - `OIDC__LOGOUTURI` (optional, needed for Auth0, use `https://{auth0-domain}/v2/logout?client_id={auth0-client-id}` with proper values for domain and client-id)
- Add *experimental* alternate schedule system
  - This allows a single playout to dynamically select a schedule based on date criteria, for example:
    - Weekday vs weekend schedules
    - Summer vs fall schedules
    - Shark week schedules
  - Alternate schedules can be managed by clicking the calendar icon in the playout list
  - Playouts contain a prioritized (top to bottom) list of alternate schedules
  - Whenever a playout is built for a given day, ErsatzTV will check for a matching schedule from top to bottom
  - A given day must match all alternate schedule parameters; wildcards (`*any*`) will always match
    - Day of week
    - Day of month
    - Month
  - The lowest priority (bottom) item will always match all parameters, and can be considered a "default" or "fallback" schedule

### Fixed
- Fix schedule editor crashing due to bad music video artist data
- Fix bug where playouts would not maintain smart collection progress on schedules that use multiple smart collections
- Fix library scanning on osx-arm64
- Fix ability to remove some media server libraries from ErsatzTV

### Changed
- Always use software pipeline for error display
  - This ensures errors will display even when hardware acceleration is misconfigured
- Call scanner process only when scanning is required based on library refresh interval
- Use lower process priority for scanner process with unforced (automatic) library scans
- Disable V2 UI and APIs by default
  - V2 UI can be re-enabled by setting the env var `ETV_UI_V2` to any value

## [0.7.2-beta] - 2023-01-05
### Fixed
- Fix VAAPI encoding in docker by switching to non-free driver

### Changed
- Rewrite log page to read directly from log files instead of sqlite

## [0.7.1-beta] - 2023-01-03
### Added
- Add new music video credit templates

### Fixed
- Fix many transcoding failures caused by the colorspace filter
- Fix song playback with VAAPI and NVENC
- Fix edge case where some local movies would not automatically be restored from trash
- Fix synchronizing Jellyfin and Emby collection items
- Fix saving some external subtitle records to database

### Changed
- Upgrade to dotnet 7
- Upgrade all docker images to ubuntu jammy and ffmpeg 5.1.2
- Limit library scan interval between 0 and 1,000,000
  - 0 means do not automatically scan libraries
  - 1 to 999,999 means scan if it has been that many hours since the last scan
- Use new `ErsatzTV.Scanner` process for scanning all libraries
  - This should reduce the ongoing memory footprint

## [0.7.0-beta] - 2022-12-11
### Fixed
- Fix removing Jellyfin and Emby libraries that have been deleted from the source media server
- Fix `Work-Ahead HLS Segmenter Limit` setting to properly limit number of channels that can work-ahead at once
- Include base path value in generated channel playlist (M3U) and channel guide (XMLTV) links
- Fix parsing song metadata from OGG audio files
- Properly unlock/re-enable trakt list operations after an operation is canceled

### Added
- Add (required) bit depth normalization option to ffmpeg profile
  - This can help if your card only supports e.g. h264 encoding, normalizing to 8 bits will allow the hardware encoder to be used
- Extract font attachments after extracting text subtitles
  - This should improve SubStation Alpha subtitle rendering
- Detect VAAPI capabilities and fallback to software decoding/encoding as needed
- Add audio stream selector scripts for episodes and movies
  - This will let you customize which audio stream is selected for playback
  - Episodes are passed the following data:
    - `channelNumber`
    - `channelName`
    - `showTitle`
    - `showGuids`: array of string ids like `imdb_1234` or `tvdb_1234`
    - `seasonNumber`
    - `episodeNumber`
    - `episodeGuids`: array of string ids like `imdb_1234` or `tvdb_1234`
    - `preferredLanguageCodes`: array of string preferred language codes configured for the channel
    - `audioStreams`: array of audio stream data, each containing
      - `index`: the stream's index number, this is what the function needs to return
      - `channels`: the number of audio channels
      - `codec`: the audio codec
      - `isDefault`: bool indicating whether the stream is flagged as default
      - `isForced`: bool indicating whether the stream is flagged as forced
      - `language`: the stream's language
      - `title`: the stream's title
  - Movies are passed the following data:
      - `channelNumber`
      - `channelName`
      - `title`
      - `guids`: array of string ids like `imdb_1234` or `tvdb_1234`
    - `preferredLanguageCodes`: array of string preferred language codes configured for the channel
    - `audioStreams`: array of audio stream data, each containing
        - `index`: the stream's index number, this is what the function needs to return
        - `channels`: the number of audio channels
        - `codec`: the audio codec
        - `isDefault`: bool indicating whether the stream is flagged as default
        - `isForced`: bool indicating whether the stream is flagged as forced
        - `language`: the stream's language
        - `title`: the stream's title
- Add new fields to search index
  - `video_codec`: the video codec
  - `video_bit_depth`: the number of bits in the video stream's pixel format, e.g. 8 or 10
  - `video_dynamic_range`: the video's dynamic range, either `sdr` or `hdr`

### Changed
- Change `Multi-Episode Shuffle` scripting system to use Javascript instead of Lua

## [0.6.9-beta] - 2022-10-21
### Fixed
- Fix bug where tail or fallback filler would sometimes schedule much longer than expected
  - This only happened with fixed start schedule items following a schedule item with tail or fallback filler
- Fix NFO reader bug that caused inaccurate warning messages about invalid XML and incomplete metadata
- Fix reverse proxy SSL termination support by supporting `X-Forwarded-Proto` header
- Fix automatic playout reset scheduling
  - Playouts would reset every 30 minutes between midnight and the configured time, instead of only at the configured time
- XMLTV: properly group schedule items with `Custom Title` followed by item(s) with `Guide Mode` set to `Filler`

### Added
- Add music video credits template system
  - Templates are selected in each channel's settings
  - Templates should be copied from `_default.ass.sbntxt` which is located in the config subfolder `templates/music-video-credits`
    - Copy the file, give it any name ending with `.ass.sbntext`, and only make edits to the copied file
  - The default template will be extracted and overwritten every time ErsatzTV is started
  - The template is an [Advanced SubStation Alpha](http://www.tcax.org/docs/ass-specs.htm) file using [scribian](https://github.com/scriban/scriban/tree/master/doc) template syntax
  - The following fields are available for use in the template:
    - `resolution`: the ffmpeg profile's resolution, which is used for margin calculations
    - `title`: the title of the music video
    - `track`: the music video's track number
    - `album`: the music video's album
    - `plot`: the music video's plot
    - `release_date`: the music video's release date
    - `artist`: the music videos artist (the parent folder)
    - `all_artists`: a list of additional artists from the music video's sidecar NFO metadata file
    - `duration`: the timespan duration of the music video, which can be used to calculate timing of additional subtitles
    - `stream_seek`: the timespan that ffmpeg will seek into the media item before beginning playback
- Add `Multi-Episode Shuffle` playout order for `Television Show` schedule items
  - The purpose of this playout order is to improve randomization for shows that normally have intro, multiple episodes, and outro
  - This playout order requires splitting the parts into individual files (e.g. splitting `s01e01-03.mkv` into `s01e01.mkv`, `s01e02.mkv` and `s01e03.mkv`)
  - This playout order requires a lua script in the config subfolder `scripts/multi-episode-shuffle`
  - The lua script should be named for the television show's guid, e.g. `tvdb_12345.lua` or `imdb_tt123456789.lua`
  - The script defines the number of parts that each un-split file typically contains
  - The script also defines a function to map each episode to a part number (or no part number i.e. `nil` if an episode has not been split)
  - All groups of part numbers (i.e. all part 1s, all part 2s) will be shuffled
  - The playout order will then schedule a random part 1 followed by a random part 2, etc
    - Un-split (`nil`) episodes will be randomly placed between re-combined parts (e.g. part1, part2, part3, un-split, part1, part2, part3)
- Add `ETV_BASE_URL` environment variable to support reverse proxies that use paths (e.g. `/ersatztv`)

### Changed
- No longer place watermarks within content by default (e.g. within 4:3 content padded to a 16:9 resolution)
  - This can be re-enabled if desired using the `Place Within Source Content` checkbox in watermark settings

## [0.6.8-beta] - 2022-10-05
### Fixed
- Fix typo introduced in `0.6.7-beta` that stopped QSV HEVC encoder from working
- Fix scaling logic for `Nvidia` acceleration and software mode
- Attempt to position watermarks within content (not over added black padding)
- Fix search results for `Other Videos` when NFO metadata is used
- Properly synchronize tags from Emby movies and shows
- Properly sync updated file paths from Plex
- Fix numeric range search queries (e.g. `minutes:[5 TO 10]`, `minutes:[* TO 3]`)

### Added
- Add `QSV Device` option to ffmpeg profile on linux
- Add guids to search index (e.g. `imdb:tt000000`, `tvdb:12345`)

## [0.6.7-beta] - 2022-09-05
### Fixed
- When all audio streams are selected with `HLS Direct`, explicitly copy them without transcoding
  - This only happens when the channel does not have a `Preferred Audio Language`
- Fix scanner crash caused by invalid mtime
- `VAAPI`: Downgrade libva from 2.15 to 2.14
- Fix bug with XMLTV that caused some filler to display with primary content details
- Multiple fixes for content scaling with `Nvidia`, `Qsv` and `Vaapi` accelerations
- Properly scale image-based subtitles
- Fix bug where a schedule containing a single item (fixed start and flood) would never finish building a playout
  - Logic was also added to detect infinite playout build loops in the future and stop them
- Fix bug where `Other Videos` wouldn't be included in scheduling mode `Shuffle In Order`

### Added
- Add `Preferred Audio Title` feature
    - Preference can be configured in channel settings and overridden on schedule items
    - When a title is specified, audio streams that contain that title (case-insensitive search) will be prioritized
    - This can be helpful for creating channels that use commentary tracks
    - External tooling exists to easily update title/name metadata if your audio streams don't already have this metadata
- Add `Amf` hardware acceleration option for AMD GPUs on Windows
- Add `QSV Extra Hardware Frames` parameter for tuning QSV acceleration
  - Performance may improve on some systems after doubling or halving the default value of `64`

## [0.6.6-beta] - 2022-08-17
### Fixed
- Use MIME Type `application/x-mpegurl` for all playlists instead of `application/vnd.apple.mpegurl`
- Replace `setsar` filter with `setdar` filter
  - `setsar` caused issues scaling between two different aspect ratios
    - For example, some 4:3 content would appear stretched when scaled to a 16:9 resolution
  - `setdar` is now only used when aspect ratios match
- Prioritize aspect ratio from container when video stream contains conflicting aspect ratio
  - This is usually caused by bad authoring, but the change should improve scaling behavior for edge cases

### Added
- Support DSD audio file formats (DFF and DSF) in local song libraries
- Support OGG audio file formats (OGG, OPUS, OGA, OGX, SPX) in local song libraries

### Changed
- Always return playlist after a maximum of 8 seconds while starting up an HLS Segmenter session
- Use multi-variant playlists instead of redirects for HLS Segmenter sessions
- Upgrade ffmpeg from 5.0 to 5.1 in most docker images (not ARM variants)
    - Upgrading from 5.0 to 5.1 is also recommended for other installations (Windows, Linux)

## [0.6.5-beta] - 2022-08-02
### Fixed
- Fix database initializer; fresh installs with v0.6.4-beta are missing some config data and should upgrade

## [0.6.4-beta] - 2022-07-28
### Fixed
- Fix subtitle stream selection when subtitle language is different than audio language
- Fix bug with unsupported AAC channel layouts
- Fix NVIDIA second-gen maxwell capabilities detection
- Return distinct search results for episodes and other videos that have the same title
  - For example, two other videos both named `Trailer` would previously have displayed as one item in search results
- Fix schedules that would begin to repeat the same content in the same order after a couple of days

### Added
- Add `640x480` resolution

## [0.6.3-beta] - 2022-07-04
### Fixed
- Maintain stream continuity when playout is rebuilt for a channel that is actively being streamed
- Properly apply changes to episode title, sort title, outline and plot from Plex
- Fix search index for other videos and songs
  - In previous versions, some libraries would incorrectly display only one item
- Properly display old versions of renamed items in trash

### Added
- Add `Minimum Log Level` option to `Settings` page
  - Other methods of configuring the log level will no longer work

## [0.6.2-beta] - 2022-06-18
### Fixed
- Fix content repeating for up to a minute near the top of every hour
- Check whether hardware-accelerated hevc codecs are supported by the NVIDIA card
  - Software codecs will be used if they are unsupported by the NVIDIA card
- Fix sorting of channel contents in EPG
- Fix Jellyfin admin user id sync
  - Ignore disabled admins and admins who do not have access to all libraries

### Added
- Add 32-bit `arm` docker tags (`develop-arm` and `latest-arm`)

### Changed
- Regularly delete old segments from transcode folder while content is actively transcoding
  - This should help reduce required disk space
  - To further minimize required disk space, set `Work-Ahead HLS Segmenter Limit` to `0` in `Settings`

## [0.6.1-beta] - 2022-06-03
### Fixed
- Fix Jellyfin show library paging
- Properly locate and identify multiple Plex servers
- Properly restore `Unavailable`/`File Not Found` items when they are located on disk

### Added
- Add basic music video credits subtitle generation
  - This can be enabled in channel settings

## [0.6.0-beta] - 2022-06-01
### Fixed
- Additional fix for duplicate `Other Videos` entries; trash may need to be emptied one last time after upgrading
- Fix watermark opacity in cultures where `,` is a decimal separator
- Rework playlist filtering to avoid empty playlist responses
- Fix some QSV/VAAPI memory errors by always requesting 64 extra hardware frames

### Added
- Enable QSV hardware acceleration for vaapi docker images

### Changed
- Use paging to synchronize all media from Plex, Jellyfin and Emby
  - This will reduce memory use and improve reliability of synchronizing large libraries
- Disable low power mode for `h264_qsv` and `hevc_qsv` encoders

## [0.5.8-beta] - 2022-05-20
### Fixed
- Fix error display with `HLS Segmenter` and `MPEG-TS` streaming modes
- Remove erroneous log messages about normalizing framerate on channels where framerate normalization is disabled
- Fix unscheduled filler gaps that sometimes happen as playouts are automatically extended each hour

### Added
- Clean transcode cache folder on startup and after `HLS Segmenter` session terminates for any reason

### Changed
- Remove thread limitation for scenarios where it is not required
  - This should give a performance boost to installations that don't use hardware acceleration
- Use hardware acceleration to display error messages where configured

## [0.5.7-beta] - 2022-05-14
### Fixed
- Reduce memory use due to library scan operations
- Fix some instances of filler getting "stuck" when a filler item is encountered that's too long for the gap
- Properly ignore Plex `Other Videos` libraries (`movie` libraries where agent is `com.plexapp.agents.none`)
- Fix `Custom Title` for schedule items with `One`, `Multiple` and `Flood` playout modes
- Fix scheduling bug where flood items would sometimes fail to continue after midnight

### Added
- Add `metadata_kind` field to search index to allow searching for items with a particular metdata source
  - Valid metadata kinds are `fallback`, `sidecar` (NFO), `external` (from a media server) and `embedded` (songs)
- Add autocomplete functionality to search bar to quickly navigate to channels, ffmpeg profiles, collections and schedules by name
- Add global setting to skip missing (file-not-found or unavailable) items when building playouts
- Add filler preset option to allow watermarks to overlay on top of filler (disabled by default)
  - This option is applied when new items are added to a playout; rebuilding is needed if you want the change to take effect immediately
- Read `track` field from music video NFO metadata and use it for chronological sorting (after release date)
- Add `Random Start Point` option to schedules
  - When this option is enabled, all `Chronological` or `Shuffle In Order` content groups will have their start points randomized
  - When this option is disabled, all `Chronological` or `Shuffle In Order` content groups will start with the chronologically earliest item

### Changed
- Replace invalid (control) characters in NFO metadata with replacement character `�` before parsing
- Store partial (incomplete) NFO metadata results when invalid XML is encountered
  - Previously, no metadata would be stored if the XML within the NFO failed to validate

## [0.5.6-beta] - 2022-05-06
### Fixed
- Fix processing local movie NFO metadata without a `year` value
- Fix processing local movie fallback metadata
- Fix search edge case where very recently added items (hours) would not be returned by relative date queries
- Fix search index validation on startup; improper validation was causing a rebuild with every startup
- Block library scanning until search index has been recreated/upgraded
- Fix occasional erroneous log messages when HLS channel playback times out because all clients have left
- Fix fallback filler playback
- Fix stream continuity when error messages are displayed
- Fix duplicate scanning within `Other Video` libraries (i.e. folders would be scanned multiple times)

### Added
- Add `show_genre` and `show_tag` to search index for seasons and episodes
- Use `aired` value to source release date from music video nfo metadata
- Add NFO metadata support to `Other Video` libraries
  - `Other Video` NFO metadata must be in the movie NFO metadata format

## [0.5.5-beta] - 2022-05-03
### Fixed
- Fix adding episodes with no title to the search index
  - This behavior was preventing some items from being removed from the trash
- Support combination NFO metadata for movies, shows, artists and music videos
  - Note that ErsatzTV does not scrape any metadata; any URLs after the XML will be ignored
- Fix bug causing some Jellyfin and Emby content to incorrectly show as unavailable
- Fix extracting embedded `mov_text` subtitles
- Properly extract embedded subtitles on playouts where subtitles are only enabled on schedule items (and not on the channel itself)

### Added
- Add experimental `arm64` docker tags (`develop-arm64` and `latest-arm64`)
- Use `Sort Title` from Movie NFO metadata if available
- Support multiple `Artist` entries in music video NFO metadata

## [0.5.4-beta] - 2022-04-29
### Fixed
- Cleanly stop all library scans when service termination is requested
- Fix health check crash when trash contains a show or a season
- Fix ability of health check crash to crash home page
- Remove and ignore Season 0/Specials from Plex shows that have no specials
- Automatically delete and rebuild the search index on startup if it has become corrupt
- Automatically scan Jellyfin and Emby libraries on startup and periodically
- Properly remove un-synchronized Plex, Jellyfin and Emby items from the database and search index
- Fix synchronizing movies within a collection from Jellyfin

### Changed
- Update Plex, Jellyfin and Emby movie and show library scanners to share a significant amount of code
  - This should help maintain feature parity going forward
- Optimize search-index rebuilding to complete 100x faster
- **No longer use network paths to source content from Jellyfin and Emby**
  - **If you previously used path replacements to convert network paths to local paths, you should remove them**

### Added
- Add `unavailable` state for Jellyfin and Emby movie and show libraries
- Add `height` and `width` to search index for all videos
- Add `season_number` and `episode_number` to search index for all episodes
- Add `season_number` to search index for seasons
- Add `show_title` to search index for seasons and episodes

## [0.5.3-beta] - 2022-04-24
### Fixed
- Cleanly stop Plex library scan when service termination is requested
- Fix bug introduced with 0.5.2-beta that prevented some Plex content from being played
- Fix spammy subtitle error message
- Fix generating blur hashes for song backgrounds in Docker

### Changed
- No longer remove Plex movies and episodes from ErsatzTV when they do not exist on disk
  - Instead, a new `unavailable` media state will be used to indicate this condition
  - After updating mounts, path replacements, etc - a library scan can be used to resolve this state

## [0.5.2-beta] - 2022-04-22
### Fixed
- Fix unlocking libraries when scanning fails for any reason
- Fix software overlay of actual size watermark

### Added
- Add support for burning in embedded and external text subtitles
  - **This requires a one-time full library scan, which may take a long time with large libraries.**
- Sync Plex, Jellyfin and Emby collections as tags on movies, shows, seasons and episodes
  - This allows smart collections that use queries like `tag:"Collection Name"`
  - Note that Emby has an outstanding collections bug that prevents updates when removing items from a collection
- Sync Plex labels as tags on movies and shows
  - This allows smart collections that use queries like `tag:"Plex Label Name"`
- Add `Deep Scan` button for Plex libraries
  - This scanning mode is *slow* but is required to detect some changes like labels

### Changed
- Improve the speed and change detection of the Plex library scanners

## [0.5.1-beta] - 2022-04-17
### Fixed
- Fix subtitles edge case with NVENC
- Only select picture subtitles (text subtitles are not yet supported)
  - Supported picture subtitles are `hdmv_pgs_subtitle` and `dvd_subtitle`
- Fix subtitles using software encoders, videotoolbox, VAAPI
- Fix setting VAAPI driver name
- Fix ffmpeg troubleshooting reports
- Fix bug where filler would behave as if it were configured to pad even though a different mode was selected
- Fix bug where mid-roll count filler would skip scheduling the final chapter in an episode

### Added
- Add `Empty Trash` button to `Trash` page

## [0.5.0-beta] - 2022-04-13
### Fixed
- Fix `HLS Segmenter` bug where it would drift off of the schedule if a playout was changed while the segmenter was running
- Ensure clients that use HDHomeRun emulation (like Plex) always get an `MPEG-TS` stream, regardless of the configured streaming mode
- Fix scheduling bug that caused some days to be skipped when fixed start times were used with fallback filler

### Added
- Add `Preferred Subtitle Language` and `Subtitle Mode` to channel settings
  - `Preferred Subtitle Language` will filter all subtitle streams based on language
  - `Subtitle Mode` will further filter subtitle streams based on attributes (forced, default)
  - If picture-based subtitles are found after filtering, they will be burned into the video stream
- Detect non-zero ffmpeg exit code from `HLS Segmenter` and `MPEG-TS`, log error output and display error output on stream
- Add `Watermark` setting to schedule items; this allows override the channel watermark. Watermark priority is:
  - Schedule Item
  - Channel
  - Global

### Changed
- Remove legacy transcoder logic option; all channels will use the new transcoder logic
- Renamed channel setting `Preferred Language` to `Preferred Audio Language`
- Reworked playout build logic to maintain collection progress in some scenarios. There are now three build modes:
  - `Continue` - add new items to the end of an existing playout
    - This mode is used when playouts are automatically extended in the background
  - `Refresh` - this mode will try to maintain collection progress while rebuilding the entire playout
    - This mode is used when a schedule is updated, or when collection modifications trigger a playout rebuild
  - `Reset` - this mode will rebuild the entire playout and will NOT maintain progress
    - This mode is only used when the `Reset Playout` button is clicked on the Playouts page
  - **This requires rebuilding all playouts, which will happen on startup after upgrading**
- Use ffmpeg to resize images; this should help reduce ErsatzTV's memory use
- Use ffprobe to check for animated logos and watermarks; this should help reduce ErsatzTV's memory use
- Allow two decimals in channel numbers (e.g. `5.73`)

## [0.4.5-alpha] - 2022-03-29
### Fixed
- Fix streaming mode inconsistencies when `mode` parameter is unspecified
- Fix startup on Windows 7

### Added
- Add option to automatically deinterlace video when transcoding
  - Previously, this was always enabled; the purpose of the option is to allow disabling any deinterlace filters
  - Note that there is no performance gain to disabling the option with progressive content; filters are only ever applied to interlaced content

### Changed
- Change FFmpeg Profile video codec and audio codec text fields to select fields
  - The appropriate video encoder will be determined based on the video format and hardware acceleration selections
- Remove FFmpeg Profile `Transcode`, `Normalize Video` and `Normalize Audio` settings
  - All content will be transcoded and have audio and video normalized
  - The only exception to this rule is `HLS Direct` streaming mode, which directly copies video and audio streams
- Always try to connect to Plex at `http://localhost:32400` even if that address isn't advertised by the Plex API
  - If Plex isn't on the localhost, all other addresses will be checked as with previous releases

## [0.4.4-alpha] - 2022-03-10
### Fixed
- Fix `HLS Direct` streaming mode
- Fix bug with `HLS Segmenter` (and `MPEG-TS`) on Windows that caused errors at program boundaries

### Added
- Perform additional duration analysis on files with missing duration metadata
- Add `nouveau` VAAPI driver option

## [0.4.3-alpha] - 2022-03-05
### Fixed
- Fix song sorting with `Chronological` and `Shuffle In Order` playback orders
- Fix watermark on scaled and/or padded video with NVIDIA acceleration
- Fix playback of interlaced mpeg2video content with NVIDIA acceleration
- Fix playback of all interlaced content with QSV acceleration
- Fix adding songs to collections from search results page
- Fix bug scheduling mid-roll filler with content that contains one chapter
  - No mid-roll filler will be inserted for content with zero or one chapters
- Fix thread sync bug with `HLS Segmenter` (and `MPEG-TS`) streaming modes
- Fix path replacement bug when media server path is left blank

### Added
- Add automated error reporting via Bugsnag
  - This can be disabled by editing the `appsettings.json` file or by setting the `Bugsnag:Enable` environment variable to `false`
- Add `album_artist` to song metadata and to search index
- Display `album_artist` on some song videos when it's different than the `artist`

### Changed
- Framerate normalization will never normalize framerate below 24fps
  - Instead, content with a lower framerate will be normalized up to 24fps
- `Shuffle In Order` will group songs by album artist instead of by track artist

## [0.4.2-alpha] - 2022-02-26
### Fixed
- Add improved but experimental transcoder logic, which can be toggled on and off in `Settings`
- Fix `HLS Segmenter` bug when source video packet contains no duration (`N/A`)
- Fix green line at the bottom of some content scaled using QSV acceleration

### Added
- Add configurable channel group (M3U) and categories (XMLTV)
- Add `Shuffle Schedule Items` option to schedule configuration
  - When this is enabled, schedule items will be shuffled rather than looped in order
  - **To support this, all playouts will be rebuilt (one time) after upgrading to this version**

### Changed
- Disable framerate normalization by default and on all ffmpeg profiles
  - If framerate normalization is desired (not typically needed), it can be re-enabled manually
- Show watermarks over songs
- Hide unused local libraries

## [0.4.1-alpha] - 2022-02-10
### Fixed
- Normalize smart quotes in search queries as they are unsupported by the search library
- Fix incorrect watermark time calculations caused by working ahead in `HLS Segmenter`
- Fix ui crash adding empty path to local library
- Fix ui crash loading collection editor
- Properly flag items as `File Not Found` when local library path (folder) is missing from disk
- Fix playback bug with unknown pixel format
- Fix playback of interlaced mpeg2video on NVIDIA, VAAPI

### Added
- Include `Series` category tag for all episodes in XMLTV
- Include movie, episode (show), music video (artist) genres as `category` tags in XMLTV
- Add framerate normalization to `HLS Segmenter` and `MPEG-TS` streaming modes
- Add `HLS Segmenter Initial Segment Count` setting to allow segmenter to work ahead before allowing client playback

### Changed
- Intermittent watermarks will now fade in and out
- Show collection name in some playout build error messages
- Use hardware-accelerated filter for watermarks on NVIDIA
- Use hardware-accelerated deinterlace for some content on NVIDIA

## [0.4.0-alpha] - 2022-01-29
### Fixed
- Fix m3u `mode` query param to properly override streaming mode for all channels
  - `segmenter` for `HLS Segmenter`
  - `hls-direct` for `HLS Direct`
  - `ts` for `MPEG-TS`
  - `ts-legacy` for `MPEG-TS (Legacy)`
  - omitting the `mode` parameter returns each channel as configured
- Link `File Not Found` health check to `Trash` page to allow deletion
- Fix `HLS Segmenter` streaming mode with multiple ffmpeg-based clients
  - Jellyfin (web) and TiviMate (Android) were specifically tested

### Added
- Hide console window on macOS and Windows; tray menu can be used to access UI, logs and to stop the app
- Also write logs to text files in the `logs` config subfolder
- Add `added_date` to search index
    - This requires rebuilding the search index and search results may be empty or incomplete until the rebuild is complete
- Add `added_inthelast`, `added_notinthelast` search field for relative added date queries
    - Syntax is a number and a unit (days, weeks, months, years) like `1 week` or `2 years`

## [0.3.8-alpha] - 2022-01-23
### Fixed
- Fix issue preventing some versions of ffmpeg (usually 4.4.x) from streaming MPEG-TS (Legacy) channels at all
  - The issue appears to be caused by using a thread count other than `1`
  - Thread count is now forced to `1` for all streaming modes other than HLS Segmenter
- Fix bug with HLS Segmenter in cultures where `.` is a group/thousands separator
- Fix search results page crashing with some media kinds
- Always use MPEG-TS or MPEG-TS (Legacy) streaming mode with HDHR (Plex)
  - Other configured modes will fall back to MPEG-TS when accessed by Plex

### Changed
- Upgrade ffmpeg from 4.4 to 5.0 in all docker images
    - Upgrading from 4.4 to 5.0 is recommended for all installations

## [0.3.7-alpha] - 2022-01-17
### Fixed
- Fix local folder scanners to properly detect removed/re-added folders with unchanged contents
- Fix double-click startup on mac
- Fix trakt list sync when show does not contain a year
- Properly unlock libraries when a scan is unable to be performed because ffmpeg or ffprobe have not been found

### Added
- Add trash system for local libraries to maintain collection and schedule integrity through media share outages
  - When items are missing from disk, they will be flagged and present in the `Media` > `Trash` page
  - The trash page can be used to permanently remove missing items from the database
  - When items reappear at the expected location on disk, they will be unflagged and removed from the trash
- Add basic Mac hardware acceleration using VideoToolbox

### Changed
- Local libraries only: when items are missing from disk, they will be added to the trash and no longer removed from collections, etc.
- Show song thumbnail in song list

## [0.3.6-alpha] - 2022-01-10
### Fixed
- Properly index `minutes` field when adding new items during scan (vs when rebuilding index)
- Fix some nvenc edge cases where only padding is needed for normalization
- Properly overwrite environment variables for ffmpeg processes (`LIBVA_DRIVER_NAME`, `FFREPORT`)

### Added
- Add music video `artist` to search index
  - This requires rebuilding the search index and search results may be empty or incomplete until the rebuild is complete

### Changed
- Remove `HLS Hybrid` streaming mode; all channels have been reconfigured to use the superior `HLS Segmenter` streaming mode
- Update `MPEG-TS` streaming mode to internally use the HLS segmenter
  - This improves compatibility with many clients and also improves performance at program boundaries
- Renamed existing `MPEG-TS` mode as `MPEG-TS (Legacy)`
  - This mode will be removed in a future release

## [0.3.5-alpha] - 2022-01-05
### Fixed
- Fix bundled ffmpeg version in base docker image (NOT nvidia or vaapi) which prevented playback since v0.3.0-alpha
- Use software decoding for mpeg4 content when VAAPI acceleration is enabled
- Fix hardware acceleration health check to recognize QSV on non-Windows platforms

### Changed
- Treat `setsar` as a hardware filter, avoiding unneeded `hwdownload` and `hwupload` steps when padding isn't required

## [0.3.4-alpha] - 2021-12-21
### Fixed
- Fix other video and song scanners to include videos contained directly in top-level folders that are added to a library
- Allow saving ffmpeg troubleshooting reports on Windows

## [0.3.3-alpha] - 2021-12-12
### Fixed
- Fix bug with saving multiple blurhash versions for cover art; all cover art will be automatically rescanned
- Fix song detail margin when no cover art exists and no watermark exists
- Fix synchronizing virtual shows and seasons from Jellyfin
- Properly sort channels in M3U

### Changed
- Use blurhash of ErsatzTV colors instead of solid colors for default song backgrounds
- Use select control instead of autocomplete control in many places
    - The autocomplete control is not intuitive to use and has focus bugs

## [0.3.2-alpha] - 2021-12-03
### Fixed
- Fix artwork upload on Windows
- Fix unicode song metadata on Windows
- Fix unicode console output on Windows
- Fix TV Show NFO metadata processing when `year` is missing
- Fix song detail outline to help legibility on white backgrounds
- Optimize song artwork scanning to prevent re-processing album artwork for each song

### Changed
- Use custom log database backend which should be more portable (i.e. work in osx-arm64)
- Use cover art blurhashes for song backgrounds instead of solid colors or box blur

## [0.3.1-alpha] - 2021-11-30
### Fixed
- Fix song page links in UI
- Show song artist in playout detail
- Include song artist and cover art in channel guide (xmltv)
- Use subtitles to display errors, which fixes many edge cases of unescaped characters
- Properly split song genre tags
- Properly display all songs that have an identical album and title
- Fix channel logo and watermark uploads
- Fix regression introduced with `v0.2.4-alpha` that caused some filler edge cases to crash the playout builder

### Added
- Add song genres to search index
- Use embedded song cover art when sidecar cover art is unavailable

### Changed
- Randomly place song cover art on left or right side of screen
- Randomly use a solid color from the cover art instead of blurred cover art for song background
- Randomly select song detail layout (large title/small artist or small artist/title/album)

## [0.3.0-alpha] - 2021-11-25
### Fixed
- Properly fix database incompatibility introduced with `v0.2.4-alpha` and partially fixed with `v0.2.5-alpha`
  - The proper fix requires rebuilding all playouts, which will happen on startup after upgrading
- Fix local library locking/progress display when adding paths
- Fix grouping duration items in EPG when custom title is configured

### Added
- Add *experimental* `Songs` local libraries
    - Like `Other Videos`, `Songs` require no metadata or particular folder layout, and will have tags added for each containing folder
    - For Example, a song at `rock/band/1990 - Album/01 whatever.flac` will have the tags `rock`, `band` and `1990 - Album`, and the title `01 whatever`
    - Songs will also have basic metadata read from embedded tags (album, artist, title)
    - Video will be automatically generated for songs using metadata and cover art or watermarks if available
- Add support for `.webm` video files

## [0.2.5-alpha] - 2021-11-21
### Fixed
- Include other video title in channel guide (xmltv)
- Fix bug introduced with 0.2.4-alpha that caused some playouts to build from year 0
- Use less memory matching Trakt list items

### Added
- Build osx-arm64 packages on release

### Changed
- No longer warn about local Plex guids; they aren't used for Trakt matching and can be ignored

## [0.2.4-alpha] - 2021-11-13
### Changed
- Upgrade to dotnet 6
- Use `scale_cuda` instead of `scale_npp` for NVIDIA scaling in all cases

## [0.2.3-alpha] - 2021-11-03
### Fixed
- Fix bug with audio filter in cultures where `.` is a group/thousands separator
- Fix bug where flood playout mode would only schedule one item
  - This would happen if the flood was followed by another flood with a fixed start time

### Added
- Support empty `.etvignore` file to instruct local movie scanner to ignore the containing folder

## [0.2.2-alpha] - 2021-10-30
### Fixed
- Fix EPG entries for Duration schedule items that play multiple items
- Fix EPG entries for Multiple schedule items that play more than one item

### Added
- Add fallback filler settings to Channel and global FFmpeg Settings
  - When streaming is attempted during an unscheduled gap, the resulting video will be determined using the following priority:
    - Channel fallback filler
    - Global fallback filler
    - Generated `Channel Is Offline` error message video

### Changed
- Allow per-episode folders for local show libraries
  - e.g. `Show Name\Season #\Episode #\Show Name - s#e#.mkv`

## [0.2.1-alpha] - 2021-10-24
### Fixed
- Fix saving dynamic start time on schedule items

## [0.2.0-alpha] - 2021-10-23
### Fixed
- Fix generated streams with mpeg2video
- Fix incorrect row count in playout detail table
- Fix deleting movies that have been removed from Jellyfin and Emby
- Fix bug that caused large unscheduled gaps in playouts
  - This was caused by schedule items with a fixed start of midnight

### Added
- Add new filler system
    - `Pre-Roll Filler` plays before each media item
    - `Mid-Roll Filler` plays between media item chapters
    - `Post-Roll Filler` plays after each media item
    - `Tail Filler` plays after all media items, until the next media item
    - `Fallback Filler` loops instead of default offline image to fill any remaining gaps
- Store chapter details with media statistics; this is needed to support mid-roll filler
    - This requires re-ingesting statistics for all media items the first time this version is launched
- Add switch to show/hide filler in playout detail table
- Add `minutes` field to search index
  - This requires rebuilding the search index and search results may be empty or incomplete until the rebuild is complete

### Changed
- Change some debug log messages to info so they show by default again
- Remove tail collection options from `Duration` playout mode
- Show localized start time in schedule items tables

## [0.1.5-alpha] - 2021-10-18
### Fixed
- Fix double scheduling; this could happen if the app was shutdown during a playout build
- Fix updating Jellyfin and Emby TV seasons
- Fix updating Jellyfin and Emby artwork
- Fix Plex, Jellyfin, Emby worker crash attempting to sync library that no longer exists
- Fix bug with `Duration` mode scheduling when media items are too long to fit in the requested duration

### Added
- Include music video thumbnails in channel guide (xmltv)

### Changed
- Automatically find working Plex address on startup
- Automatically select schedule item in schedules that contain only one item
- Change default log level from `Debug` to `Information`
  - The `Debug` log level can be enabled in the `appsettings.json` file for non-docker installs
  - The `Debug` log level can be enabled by setting the environment variable `Serilog:MinimumLevel=Debug` for docker installs

## [0.1.4-alpha] - 2021-10-14
### Fixed
- Fix error message/offline stream continuity with channels that use HLS Segmenter
- Fix removing items from search index when folders are removed from local libraries

### Added
- Add `Other Video` local libraries
  - Other video items require no metadata or particular folder layout, and will have tags added for each containing folder
  - For Example, a video at `commercials/sd/1990/whatever.mkv` will have the tags `commercials`, `sd` and `1990`, and the title `whatever`
- Add filler `Tail Mode` option to `Duration` playout mode (in addition to existing `Offline` option)
  - Filler collection will always be randomized (to fill as much time as possible)
  - Filler will be hidden from channel guide, but visible in playout details in ErsatzTV
  - Unfilled time will show offline image
- Add `Guide Mode` option to all schedule items
  - `Normal` guide mode will show all scheduled items in the channel guide (xmltv)
  - `Filler` guide mode will hide all scheduled items from the channel guide, and extend the end time for the previous item in the guide

## [0.1.3-alpha] - 2021-10-13
### Fixed
- Fix startup bug for some docker installations

## [0.1.2-alpha] - 2021-10-12
### Added
- Include more cuda (nvidia) filters in docker image
- Enable deinterlacing with nvidia using new `yadif_cuda` filter
- Add two HLS Segmenter settings: idle timeout and work-ahead limit
  - `HLS Segmenter Idle Timeout` - the number of seconds to keep transcoding a channel while no requests have been received from any client
    - This setting must be greater than or equal to 30 (seconds)
  - `Work-Ahead HLS Segmenter Limit` - the number of segmenters (channels) that will work-ahead simultaneously (if multiple channels are being watched)
    - "working ahead" means transcoding at full speed, which can take a lot of resources
    - This setting must be greater than or equal to 0
- Add more watermark locations ("middle" of each side)
- Add `VAAPI Device` setting to ffmpeg profile to support installations with multiple video cards
- Add *experimental* `RadeonSI` option for `VAAPI Driver` and include mesa drivers in vaapi docker image

### Changed
- Upgrade ffmpeg from 4.3 to 4.4 in all docker images
  - Upgrading from 4.3 to 4.4 is recommended for all installations
- Move `VAAPI Driver` from settings page to ffmpeg profile to support installations with multiple video cards

### Fixed
- Fix some transcoding edge cases with nvidia and pixel format `yuv420p10le`

## [0.1.1-alpha] - 2021-10-10
### Added
- Add music video album to search index
  - This requires rebuilding the search index and search results may be empty or incomplete until the rebuild is complete

### Changed
- Remove forced initial delay from `HLS Segmenter` streaming mode
- Upgrade nvidia docker image from 18.04 to 20.04

## [0.1.0-alpha] - 2021-10-08
### Added
- Add *experimental* streaming mode `HLS Segmenter` (most similar to `HLS Hybrid`)
  - This mode is intended to increase client compatibility and reduce issues at program boundaries
  - If you want the temporary transcode files to be located on a particular drive, the docker path is `/root/.local/share/etv-transcode`
- Store frame rate with media statistics; this is needed to support HLS Segmenter
  - This requires re-ingesting statistics for all media items the first time this version is launched

### Changed
- Use latest iHD driver (21.2.3 vs 20.1.1) in vaapi docker images

### Fixed
- Add downsampling to support transcoding 10-bit HEVC content with the h264_vaapi encoder
- Fix updating statistics when media items are replaced
- Fix XMLTV generation when scheduled episode is missing metadata

## [0.0.62-alpha] - 2021-10-05
### Added
- Support IMDB ids from Plex libraries, which may improve Trakt matching for some items

### Fixed
- Include Specials/Season 0 `episode-num` entry in XMLTV
- Fix some transcoding edge cases with VAAPI and pixel formats `yuv420p10le`, `yuv444p10le` and `yuv444p`
- Update Plex movie and episode paths when they are changed within Plex
- Always use `libx264` software encoder for error messages

## [0.0.61-alpha] - 2021-09-30
### Fixed
- Revert nvenc/cuda filter change from v60

## [0.0.60-alpha] - 2021-09-25
### Added
- Add Trakt list support under `Lists` > `Trakt Lists`
  - Trakt lists can be added by url or by `user/list`
  - To re-download a Trakt list, simply add it again (no need to delete)
  - See `Logs` for unmatched item details
  - Trakt lists can only be scheduled by using Smart Collections
- Add seasons to search index
  - This is needed because Trakt lists can contain seasons
  - This requires rebuilding the search index and search results may be empty or incomplete until the rebuild is complete

### Fixed
- Fix local television scanner to properly update episode metadata when NFO files have been added/changed
- Properly detect ffmpeg nvenc (cuda) support in Hardware Acceleration health check
- Fix nvenc/cuda filter for some yuv420p content

## [0.0.59-alpha] - 2021-09-18
### Added
- Add `Health Checks` table to home page to identify and surface common misconfigurations
  - `FFmpeg Version` checks `ffmpeg` and `ffprobe` versions
  - `FFmpeg Reports` checks whether ffmpeg troubleshooting reports are enabled since they can use a lot of disk space over time
  - `Hardware Acceleration` checks whether channels that transcode are using acceleration methods that ffmpeg claims to support
  - `Movie Metadata` checks whether all movies have metadata (fallback metadata counts as metadata)
  - `Episode Metadata` checks whether all episodes have metadata (fallback metadata counts as metadata)
  - `Zero Duration` checks whether all movies and episodes have a valid (non-zero) duration
  - `VAAPI Driver` checks whether a vaapi driver preference is configured when using the vaapi docker image
- Add setting to each playout to schedule an automatic daily rebuild
  - This is useful if the playout uses a smart collection with `released_onthisday`

### Fixed
- Fix docker vaapi support for newer Intel platforms (Gen 8+)
  - This includes a new setting to force a particular vaapi driver (`iHD` or `i965`), as some Gen 8 or 9 hardware that is supported by both drivers will perform better with one or the other
- Fix scanning and indexing local movies and episodes without NFO metadata
- Fix displaying seasons for shows with no year (in metadata or in folder name)
- Fix "direct play" in MPEG-TS mode (copy audio and video stream when `Transcode` is unchecked)

## [0.0.58-alpha] - 2021-09-15
### Added
- Add `released_notinthelast` search field for relative release date queries
  - Syntax is a number and a unit (days, weeks, months, years) like `1 week` or `2 years`
- Add `released_onthisday` search field for historical queries
  - Syntax is `released_onthisday:1` and will search for items released on this month number and day number in prior years
- Add tooltip explaining `Keep Multi-Part Episodes Together`

### Fixed
- Properly display watermark when no other video filters (like scaling or padding) are required
- Fix building some playouts in timezones with positive offsets (like UTC+2)
- Fix `Shuffle In Order` so all collections/shows start from the earliest episode
  - You may need to rebuild playouts to see this fixed behavior more quickly

## [0.0.57-alpha] - 2021-09-10
### Added
- Add `released_inthelast` search field for relative release date queries
  - Syntax is a number and a unit (days, weeks, months, years) like `1 week` or `2 years`
- Allow adding smart collections to multi collections

### Fixed
- Fix loading artwork in Kodi
  - Use fake image extension (`.jpg`) for artwork in M3U and XMLTV since Kodi detects MIME type from URL
  - Enable HEAD requests for IPTV image paths since Kodi requires those

## [0.0.56-alpha] - 2021-09-10
### Added
- Add Smart Collections
  - Smart Collections use search queries and can be created from the search result page
  - Smart Collections are re-evaluated every time playouts are extended or rebuilt to automatically include newly-matching items
  - This requires rebuilding the search index and search results may be empty or incomplete until the rebuild is complete
- Allow `Shuffle In Order` with Collections and Smart Collections
  - Episodes will be grouped by show, and music videos will be grouped by artist
  - All movies will be a single group (multi-collections are probably better if `Shuffle In Order` is desired for movies)
  - All groups will be ordered chronologically (custom ordering is only supported in multi-collections)

### Fixed
- Generate XMLTV that validates successfully
  - Properly order elements
  - Omit channels with no programmes
  - Properly identify channels using the format number.etv like `15.etv`
- Fix building playouts when multi-part episode grouping is enabled and episodes are missing metadata
- Fix incorrect total items count in `Multi Collections` table

## [0.0.55-alpha] - 2021-09-03
### Fixed
- Fix all local library scanners to ignore dot underscore files (`._`)

## [0.0.54-alpha] - 2021-08-21
### Added
- Add `Shuffle In Order` playback order for multi-collections.
  - This is useful for randomizing multiple collections/shows on a single channel, while each collection maintains proper ordering (custom or chronological)

### Fixed
- Fix bug parsing ffprobe output in cultures where `.` is a group/thousands separator
  - This bug likely prevented ETV from scheduling correctly or working at all in those cultures
  - After installing a version with this fix, affected content will need to be removed from ETV and re-added

## [0.0.53-alpha] - 2021-08-01
### Fixed
- Fix error message displayed after building empty playout
- Fix Emby and Jellyfin links

### Changed
- Always proxy Jellyfin and Emby artwork; this fixes artwork in some networking scenarios

## [0.0.52-alpha] - 2021-07-22
### Added
- Add multiple local libraries to better organize your media
- Add `Move Library Path` function to support reorganizing existing local libraries

### Fixed
- Fix bug preventing playouts from rebuilding after an empty collection is encountered within a multi-collection

## [0.0.51-alpha] - 2021-07-18
### Added
- Add `Multi Collection` to support shuffling multiple collections within a single schedule item
  - Collections within a multi collection are optionally grouped together and ordered when scheduling; this can be useful for franchises
- Add `Playout Days To Build` setting to control how many days of playout data/program guide data should be built into the future

### Changed
- Move `Playback Order` from schedule to schedule items
  - This allows different schedule items to have different playback orders within a single schedule

### Fixed
- Fix release notes on home page with `-alpha` suffix
- Fix linux-arm release by including SQLite interop artifacts
- Fix issue where cached Plex credentials may become invalid when multiple servers are used

## [0.0.50-alpha] - 2021-07-13
### Added
- Add Linux ARM release artifacts which can be used on Raspberry Pi devices

### Fixed
- Fix bug preventing ingestion of local movies with fallback metadata (without NFO files)
- Fix extra spaces in titles of local movies with fallback metadata (without NFO files)

## [0.0.49-prealpha] - 2021-07-11
### Added
- Include audio language metadata in all streaming modes
- Add special zero-count case to `Multiple` playout mode
  - This configuration will automatically maintain the multiple count so that it is equal to the number of items in the collection
  - This configuration should be used if you want to play every item in a collection exactly once before advancing

### Changed
- Use case-insensitive sorting for collections page and `Add to Collection` dialog
- Use case-insensitive sorting for all collection lists in schedule items editor
- Use natural sorting for schedules page and `Add to Schedule` dialog

### Fixed
- Fix flooding schedule items that have a fixed start time

## [0.0.48-prealpha] - 2021-06-22
### Added
- Store pixel format with media statistics; this is needed to support normalization of 10-bit media items
  - This requires re-ingesting statistics for all media items the first time this version is launched

### Changed
- Use ffprobe to retrieve statistics for Plex media items (Local, Emby and Jellyfin libraries already use ffprobe)

### Fixed
- Fix playback of transcoded 10-bit media items (pixel format `yuv420p10le`) on Nvidia hardware
- Emby and Jellyfin scanners now respect library refresh interval setting
- Fix adding new seasons to existing Emby and Jellyfin shows
- Fix adding new episodes to existing Emby and Jellyfin seasons

## [0.0.47-prealpha] - 2021-06-15
### Added
- Add warning during playout rebuild when schedule has been emptied
- Save Logs, Playout Detail, Schedule Detail table page sizes

### Changed
- Show all log entries in log viewer, not just most recent 100 entries
- Use server-side paging and sorting for Logs table
- Use server-side paging for Playout Detail table
- Remove pager from Schedule Items editor (all schedule items will always be displayed)

### Fixed
- Fix ui crash adding a channel without a watermark
- Clear playout detail table when playout is deleted
- Fix blazor error font color
- Fix some audio stream languages missing from UI and search index
- Fix audio stream selection for languages with multiple codes
- Fix searching when queries contain non-ascii characters

## [0.0.46-prealpha] - 2021-06-14
### Added
- Add watermark opacity setting to allow blending with content
- Add global watermark setting; channel-specific watermarks have precedence over global watermarks
- Save Schedules, Playouts table page sizes

### Changed
- Remove unused API and SDK project; may reintroduce in the future but for now they have fallen out of date
- Rework watermarks to be separate from channels (similar to ffmpeg profiles)
  - **All existing watermarks have been removed and will need to be recreated using the new page**
  - This allows easy watermark reuse across channels

### Fixed
- Fix ui crash adding or editing schedule items due to Artist with no name
- Fix many potential sources of inconsistent data in UI

## [0.0.45-prealpha] - 2021-06-12
### Added
- Add experimental `HLS Hybrid` channel mode
  - Media items are transcoded using the channel's ffmpeg profile and served using HLS
- Add optional channel watermark

### Changed
- Remove framerate normalization; it caused more problems than it solved
- Include non-US (and unknown) content ratings in XMLTV

### Fixed
- Fix serving channels.m3u with missing content ratings
- Fix percent progress indicator for Jellyfin and Emby show library scans

## [0.0.44-prealpha] - 2021-06-09
### Added
- Add artists directly to schedules
- Include MPAA and VCHIP content ratings in XMLTV guide data
- Quickly skip missing files during Plex library scan

### Fixed
- Ignore unsupported plex guids (this prevented some libraries from scanning correctly)
- Ignore unsupported STRM files from Jellyfin

## [0.0.43-prealpha] - 2021-06-05
### Added
- Support `(Part #)` name suffixes for multi-part episode grouping
- Support multi-episode files in local and Plex libraries
- Save Channels table page size
- Add optional query string parameter to M3U channel playlist to allow some customization per client
    - `?mode=ts` will force `MPEG-TS` mode for all channels
    - `?mode=hls-direct` will force `HLS Direct` mode for all channels
    - `?mode=mixed` or no parameter will maintain existing behavior

### Changed
- Rename channel mode `TransportStream` to `MPEG-TS` and `HttpLiveStreaming` to `HLS Direct`
- Improve `HLS Direct` mode compatibility with Channels DVR Server

### Fixed
- Fix search result crashes due to missing season metadata

## [0.0.42-prealpha] - 2021-05-31
### Added
- Support roman numerals and english integer names for multi-part episode grouping
- Add option to treat entire collection as a single show with multi-part episode grouping
    - This is useful for multi-part episodes that span multiple shows (crossovers)

### Changed
- Skip zero duration items when building a playout, rather than aborting the playout build

### Fixed
- Fix edge case where a playout rebuild would get stuck and block all other playouts and local library scans

## [0.0.41-prealpha] - 2021-05-30
### Added
- Add button to refresh list of Plex, Jellyfin, Emby libraries without restarting app
- Add episodes to search index
- Add director and writer metadata to episodes
- Add unique id/provider id metadata, which will support future features
- Allow grouping multi-part episodes with titles ending in `Part X`, `Part Y`, etc.

### Changed
- Change home page link from release notes to full changelog

### Fixed
- Fix missing channel logos after restart
- Fix multi-part episode grouping with missing episodes/parts
- Fix multi-part episode grouping in collections containing multiple shows
- Fix updating modified seasons and episodes from Jellyfin and Emby

## [0.0.40-prealpha] - 2021-05-28
### Added
- Add content rating metadata to movies and shows
- Add director and writer metadata to movies
- Sync tv show thumbnail artwork in Local, Jellyfin and Emby libraries (*not* Plex)
- Prioritize tv show thumbnail artwork over tv show posters in XMLTV
- Include tv show artwork in XMLTV when grouped items with custom title are all from the same show
- Cache resized local artwork on disk

### Fixed
- Recursively retrieve Jellyfin and Emby items
- Fix incorrect search item counts
- Fix stack trace information in non-docker releases
- Fix crash opening `Add to Schedule` dialog
- Disable FFmpeg troubleshooting reports on Windows as they do not work properly

## [0.0.39-prealpha] - 2021-05-25
### Added
- Show Jellyfin and Emby artwork in XMLTV

### Fixed
- Fix path replacements for Jellyfin and Emby, including UNC paths
- Use Emby path replacements for playback
- Fix playback when `fps` is the only required filter
- Fix resources (images, fonts) required to display offline channel message

## [0.0.38-prealpha] - 2021-05-23
### Added
- Add support for Emby
- Use "single-file" deployments for releases
    - Non-docker releases will have significantly fewer files
    - It is recommended to empty your installation folder before copying in the latest release.

### Fixed
- Fix some cases where Jellyfin artwork would not display
- Fix saving schedule items with duration less than one hour
- Use ffmpeg 4.3 in docker images; there was a performance regression with 4.4 (only in docker)

## [0.0.37-prealpha] - 2021-05-21
### Added
- Add option to keep multi-part episodes together when shuffling (i.e. two-part season finales)
- Optimize Plex TV Scanner to quickly process shows that have not been updated since the last scan
- Optimize local Movie, Show, Music Video scanners to quickly skip unchanged folders, and to notice any mtime change
- Add server binding configuration to `appsettings.json` which lets non-docker installations bind to localhost or change the port number

### Fixed
- Properly ignore `Other` Jellyfin libraries
- Fix bug where search index would try to re-initialize unnecessarily
- Fix one cause of green line at bottom of some transcoded videos by forcing even scaling targets

## [0.0.36-prealpha] - 2021-05-16
### Added
- Add support for Jellyfin
- Add support for ffmpeg 4.4, and use ffmpeg 4.4 in all docker images
- Add configurable library refresh interval
- Add button to copy/clone ffmpeg profile

## [0.0.35-prealpha] - 2021-04-27
### Added
- Add search button for each library in `Libraries` page to quickly filter content by library
    - This requires rebuilding the search index and search results may be empty or incomplete until the rebuild is complete

### Fixed
- Fix ingesting actors and actor artwork from newly-added Plex media items
- Only show `movie` and `show` libraries from Plex. Other library types are not supported at this time.
- Fix local movie scanner missing replaced/updated files

## [0.0.34-prealpha] - 2021-04-17
### Added
- Allow `enter` key to submit all dialogs
- Add actors to movies and shows (Plex or NFO metadata is required)
    - Note that this requires a one-time full library scan to ingest actor metadata, which may take a long time with large libraries.
- Rework metadata list links in UI (languages, studios, genres, etc)

### Fixed
- Fix EPG generation with music video channels that do not use a custom title
- Fix lag when typing in search bar, `Add To Collection` dialog
- Fix collections paging
- Fix padding odd resolutions (this bug caused some items to always fail playback)
- Only update Plex episode artwork as needed

## [0.0.33-prealpha] - 2021-04-11
### Added
- Add language buttons to movies, shows, artists
- Show release notes on home page

### Fixed
- Re-import missing television metadata that was incorrectly removed with `v0.0.32`
- Fix language indexing; language searches now use full english name
- Fix synchronizing television studios, genres from Plex
- Limit channels to one playout per channel
    - Though more than one playout was previously possible it was unsupported and unlikely to work as expected, if at all
    - A future release may make this possible again

## [0.0.32-prealpha] - 2021-04-09
### Added
- `Add All To Collection` button to quickly add all search results to a collection
- Add Artists scanned from Music Video libraries
    - Artist folders are now required, but music videos now have no naming requirements
    - `artist.nfo` metadata is supported along with thumbnail and poster artwork
- Save Collections table page size in local storage

### Fixed
- Fix audio stream language indexing for movies and music videos
- Fix synchronizing list of Plex servers and connection addresses for each server
- Fix `See All` link for music video search results

## [0.0.31-prealpha] - 2021-04-06
### Added
- Add documentation link to UI
- Add `language` search field
- Minor log viewer improvements
- Use fragment navigation with letter bar (clicking a letter will page and scroll until that letter is in view)
- Send all audio streams with HLS when channel has no preferred language
- Move FFmpeg settings to new `Settings` page
- Add HDHR tuner count setting to new `Settings` page

### Fixed
- Fix poster width
- Fix bug that would occasionally prevent items from being added to the search index
- Automatically refresh the Plex Media Sources page after sign in or sign out

## 0.0.30-prealpha [YANKED]

## [0.0.29-prealpha] - 2021-04-04
- No longer require NFO metadata for music videos
    - Instead, the only requirement is that music video files be named `[artist] - [track].[extension]` where the three characters (space dash space) between artist and track are required
- Add library scan progress detail
- Optimize library scans after adding library path to only scan new library path
- Fix bug replacing music videos
- Scan Plex libraries and local libraries on different threads
- Use English names for preferred languages in UI instead of ISO language code

## [0.0.28-prealpha] - 2021-04-03
- Apply audio normalization more consistently; this should further reduce program boundary errors
- Replace unused audio volume setting with audio loudness normalization option
    - This can be particularly helpful with music video channels if media items have inconsistent loudness
    - This setting may be less desirable on movie channels where loudness is intended to be dynamic
- Fix XMLTV containing music videos that do not use a custom title
- Fix channels table sorting, add paging to channels table
- Add sorting and paging to schedules table
- Add paging to playouts table
- Use table instead of cards for collections view

## [0.0.27-prealpha] - 2021-04-02
- Add ***basic*** music video library support
    - **NFO metadata is required for music videos** - see [tags](https://kodi.wiki/view/NFO_files/Music_videos#Music_Video_Tags), [template](https://kodi.wiki/view/NFO_files/Music_videos#Template_nfo) and [sample](https://kodi.wiki/view/NFO_files/Music_videos#Sample_nfo)
    - Artists can be searched using the `artist` field, like `artist:daft`
- Clear search query when clicking `Movies` or `TV Shows` from paged search results
- Add show title to playout details
- Let ffmpeg determine thread count by default (signified by `0` threads in ffmpeg profile)
- Save troubleshooting reports for ffmpeg concat process in addition to transcode process
- Simplify ffmpeg normalization options
- Add frame rate setting to ffmpeg profile
    - When video normalization is enabled, all media items will have their frame rate converted to the same value
- Fix some scenarios where streaming would freeze at program boundaries
- Fix bug preventing some Plex libraries from scanning
- Fix bug preventing some local libraries from scanning folders that were recently added

## [0.0.26-prealpha] - 2021-03-30
- Add `Custom Title` option to schedule items
    - When a custom title is set, the schedule item will be grouped in the EPG with the custom title
- Navigate to schedule items after creating new schedule
- Fix channel editor so preferred language is no longer required on every channel
- Fix bug with audio track selection during non-normalized playback
- Fix bug with playout builds where `Multiple` or `Duration` items wouldn't respect the settings over time
- Fix bug that prevented some television folders from scanning

## [0.0.25-prealpha] - 2021-03-29
- Add preferred language feature
    - Global preference can be set in FFmpeg settings; channels can override global preference
    - Preferences require [ISO 639-2](https://en.wikipedia.org/wiki/List_of_ISO_639-2_codes) language codes
    - Audio stream selection will attempt to respect these preferences and prioritize streams with the most channels
    - English (`eng`) will be used as a fallback when no preferences have been configured
    - ***This feature requires a one-time reanalysis of every media item which may take a long time for large libraries and playback may fail until this scan has completed***
- Fix channel sorting in EPG
- Fix mixed-platform path replacements (Plex on Windows with ErsatzTV on Linux, or Plex on Linux with ErsatzTV on Windows)
- Fix local television library scanning; this was broken with `v0.0.23`
- Optimize local library scanning; regular scans should be significantly faster
- Add log warning when a zero-duration media item is encountered
- Fix indexing local shows without NFO metadata.
    - If you have this issue the best way to fix is to:
        - Shutdown ErsatzTV
        - Delete the `search-index` subfolder inside the ErsatzTV config folder
        - Start ErsatzTV; the full search index will be rebuilt on startup
- Fix updating search index when genres, tags, studios are updated in local libraries
- Adjust artwork routes so all IPTV traffic can be proxied with a single path prefix of `/iptv`

## [0.0.24-prealpha] - 2021-03-22
- Fix a critical bug preventing library synchronization with Plex sign ins performed with `v0.0.22` or `v0.0.23`
    - **If you are unable to sync libraries from Plex, please sign out and back in to apply this fix**
- Fallback to `folder.jpg` when `poster.jpg` is not present
- Attach episodes to correct show when adding NFO metadata to existing libraries

## [0.0.23-prealpha] - 2021-03-21
- Remove all Plex items from search index after sign out
- Fix fallback metadata for local episodes (episode number was missing)
- Improve television show year detection where year is missing from nfo metadata
- Fix sorting for titles that start with `A` or `An` in addition to `The`
- Properly escape search links containing special characters (genre, tag)
- Add and index `Studio` metadata

## [0.0.22-prealpha] - 2021-03-20
- Log errors encountered during search index build; attempt to continue with partial index when errors are encountered
- Only search `title` field by default; `genre` and `tag` can be searched with `field:query` syntax
- Allow leading wildcards in searches
- Keep search query in search field to allow easy modification
- Fix default ffmpeg profile when creating new channels
- Fix multiple bugs with updating Plex servers, libraries, path replacements
- Add `release_date` to search index

## [0.0.21-prealpha] - 2021-03-20
- Optimize local library scanning to use less memory
- Duplicate some documentation near the schedule item editor
- Fix bug with updating `Normalize Video Codec` setting
- Rework search functionality
    - Search landing page will show up to 50 items of each type
    - `See All` links can be used to page through all search results
    - Complex search queries supported (`christmas OR santa`)
    - Fields that are searched by default:
        - `title`
        - `genre`
        - `tag`
    - Fields that aren't searched by default, but can be included in queries with syntax like (`plot:whatever`):
        - `plot`
        - `library_name`
        - `type` (`movie` or `show`)
    - Add letter bar to all paged search results to quickly navigate to a particular letter

## [0.0.20-prealpha] - 2021-03-17
- Fix NVIDIA hardware acceleration in `develop-nvidia` and `latest-nvidia` Docker tags
    - This may never have worked correctly in Docker with older releases
- Fix occasional crash rebuilding playout from ui
- Fix crash adding a channel when no channels exist
- Fix playback for media containing attached pictures

## [0.0.19-prealpha] - 2021-03-16
- Regularly scan Plex libraries (same as local libraries)
- Add ability to create new collection from `Add to Collection` dialog
- Fix channel logos in XMLTV
- Add episode posters (show posters) to XMLTV
- Fix shuffled schedules from occasionally having repeated items when reshuffling
    - This was more likely to happen with low-cardinality collections like A B C C A B B C A
- Add optional FFmpeg troubleshooting reports
- Allow synchronizing hidden Plex libraries

## [0.0.18-prealpha] - 2021-03-14
- Plex is now a supported media source
    - Plex is **not** used for transcoding at this point, files are played directly from the filesystem using ErsatzTV transcoding
    - Path replacements will be needed if your shared media folders are mounted differently in Plex and ErsatzTV

## [0.0.17-prealpha] - 2021-03-13
- Fix bug introduced with 0.0.16 that prevented some playouts from building
- Properly set sort title on added tv shows
- Fix loading season pages containing episodes that have incomplete metadata
- Improve XMLTV guide data

## [0.0.16-prealpha] - 2021-03-12
- Fix infinite loop caused by incorrectly configured ffprobe path
- Add more strict ffmpeg and ffprobe settings validation
- Add custom playback order option to collections that contain only movies
    - This custom playback order will override the schedule's configured playback order for the collection

## [0.0.15-prealpha] - 2021-03-11
- Update UI for tv shows
- Fix tv show sorting
- Fix editing channel numbers
- Fix playout timezone bugs
- Add searchable genres and tags from local NFO metadata
- Add multi-select feature to movies, shows, search results and collection items pages

## [0.0.14-prealpha] - 2021-03-09
- New movie layout utilizing fan art (if available)
- New dark UI
- Fix offline stream (displayed when no media is scheduled for playback)
- Add M3U codec hints for Channels DVR
- Allow sub-channel numbers
- Fix bug where ffmpeg wouldn't terminate after a media item completed playback
- Fix time zone in new docker base images
- Fix vaapi pipeline with mpeg4 content by using software decoder with hardware encoder
- Enforce unique schedule name
- Enforce unique channel number
- Fix sorting of collection items in UI

## [0.0.13-prealpha] - 2021-03-07
- Remember selected Collection in `Add To Collection` dialog
- Automatically rebuild Playouts referencing any Collection that has items added or removed from the UI
- Remove Media Items from database when files are removed from disk
- Add hardware-accelerated transcoding support (`qsv`, `nvenc`/`nvidia`, `vaapi`)
    - All flavors support resolution normalization (scaling and padding)
    - This requires support within ffmpeg; see README for new docker image tags

## [0.0.12-prealpha] - 2021-03-02
- Fix a database migration issue introduced with version 0.0.11
- Shutdown app when database migration failures are encountered at startup

## [0.0.11-prealpha] - 2021-03-01
- Add Libraries and Library Paths under Media Sources
    - Two local libraries exist: `Movies` and `Shows`
    - Local Media Sources from prior versions are now found under Library Paths
- Add `Rebuild Playout` buttons to quickly regenerate playouts after modifying collections
- Add `Add to Collection` buttons to most media cards (movies, shows, seasons, episodes)
- Add Search page for searching movies and shows

## [0.0.10-prealpha] - 2021-02-21
- Rework how television media is stored in the database
- Rework how media is linked to a collection
- Add season, episode and movie detail views to UI
- Add media to collections and schedules from detail views
- Easily add and remove media from a collection
- Easily add and reorder schedule items

## [0.0.9-prealpha] - 2021-02-15
- Local media scanner has been rewritten and is much more performant
- Ignore extras in the same folder as movies (`-behindthescenes`, `-trailer`, etc)
- Support `movie.nfo` metadata in addition to matching filename nfo metadata
- Changes to video files, metadata and posters are automatically detected and used

## [0.0.8-prealpha] - 2021-02-14
- Optimize scanning so playouts are only rebuilt when necessary (duration changes, or collection membership changes)
- Automatically add new posters during scanning
- Support more poster file types (jpg, jpeg, png, gif, tbn)
- Add "Refresh All Metadata" button to media sources page; this should only be needed if NFO metadata or posters are modified
- Add progress indicator for media sources that are being actively scanned
- Prevent deleting media source during scan
- Prevent creating playout with empty schedule

## [0.0.7-prealpha] - 2021-02-13
- Rework media items layout - table has been replaced with cards/posters
- Fix bug preventing long folder names from being used as media sources
- Use 24h time pickers in schedule editor

## [0.0.6-prealpha] - 2021-02-12
- Add version information to UI
- Add basic log viewer to UI

## [0.0.5-prealpha] - 2021-02-12
- Fix bug where media scanner could stop prematurely and miss media items
- Add database migrations

## [0.0.4-prealpha] - 2021-02-11
- **Fix HDHomeRun routes** - this version is required to use as a DVR with Plex, older versions will not work
- Improve metadata parsing for tv, add fallback (filename) parsing for movies

## [0.0.3-prealpha] - 2021-02-11
- Fix incomplete XML issue introduced with v0.0.2-prealpha
- Add `.ts` files to local media scanner
- Change M3U, XMLTV, API icons to text links

## 0.0.2-prealpha - 2021-02-11 [YANKED]
- Relax some searches to be case-insensitive
- Improve categorization of tv episodes without sidecar metadata
- Properly escape XML content in XMLTV

## [0.0.1-prealpha] - 2021-02-10
- Initial release to facilitate testing outside of Docker.


[Unreleased]: https://github.com/ErsatzTV/legacy/compare/v26.5.1...HEAD
[26.5.1]: https://github.com/ErsatzTV/legacy/compare/v26.5.0...v26.5.1
[26.5.0]: https://github.com/ErsatzTV/legacy/compare/v26.4.0...v26.5.0
[26.4.0]: https://github.com/ErsatzTV/legacy/compare/v26.3.0...v26.4.0
[26.3.0]: https://github.com/ErsatzTV/legacy/compare/v26.2.0...v26.3.0
[26.2.0]: https://github.com/ErsatzTV/legacy/compare/v26.1.1...v26.2.0
[26.1.1]: https://github.com/ErsatzTV/legacy/compare/v26.1.0...v26.1.1
[26.1.0]: https://github.com/ErsatzTV/legacy/compare/v25.9.0...v26.1.0
[25.9.0]: https://github.com/ErsatzTV/legacy/compare/v25.8.0...v25.9.0
[25.8.0]: https://github.com/ErsatzTV/legacy/compare/v25.7.1...v25.8.0
[25.7.1]: https://github.com/ErsatzTV/legacy/compare/v25.7.0...v25.7.1
[25.7.0]: https://github.com/ErsatzTV/legacy/compare/v25.6.0...v25.7.0
[25.6.0]: https://github.com/ErsatzTV/legacy/compare/v25.5.0...v25.6.0
[25.5.0]: https://github.com/ErsatzTV/legacy/compare/v25.4.0...v25.5.0
[25.4.0]: https://github.com/ErsatzTV/legacy/compare/v25.3.1...v25.4.0
[25.3.1]: https://github.com/ErsatzTV/legacy/compare/v25.3.0...v25.3.1
[25.3.0]: https://github.com/ErsatzTV/legacy/compare/v25.2.0...v25.3.0
[25.2.0]: https://github.com/ErsatzTV/legacy/compare/v25.1.0...v25.2.0
[25.1.0]: https://github.com/ErsatzTV/legacy/compare/v0.8.8-beta...v25.1.0
[0.8.8-beta]: https://github.com/ErsatzTV/legacy/compare/v0.8.7-beta...v0.8.8-beta
[0.8.7-beta]: https://github.com/ErsatzTV/legacy/compare/v0.8.6-beta...v0.8.7-beta
[0.8.6-beta]: https://github.com/ErsatzTV/legacy/compare/v0.8.5-beta...v0.8.6-beta
[0.8.5-beta]: https://github.com/ErsatzTV/legacy/compare/v0.8.4-beta...v0.8.5-beta
[0.8.4-beta]: https://github.com/ErsatzTV/legacy/compare/v0.8.3-beta...v0.8.4-beta
[0.8.3-beta]: https://github.com/ErsatzTV/legacy/compare/v0.8.2-beta...v0.8.3-beta
[0.8.2-beta]: https://github.com/ErsatzTV/legacy/compare/v0.8.1-beta...v0.8.2-beta
[0.8.1-beta]: https://github.com/ErsatzTV/legacy/compare/v0.8.0-beta...v0.8.1-beta
[0.8.0-beta]: https://github.com/ErsatzTV/legacy/compare/v0.7.9-beta...v0.8.0-beta
[0.7.9-beta]: https://github.com/ErsatzTV/legacy/compare/v0.7.8-beta...v0.7.9-beta
[0.7.8-beta]: https://github.com/ErsatzTV/legacy/compare/v0.7.7-beta...v0.7.8-beta
[0.7.7-beta]: https://github.com/ErsatzTV/legacy/compare/v0.7.6-beta...v0.7.7-beta
[0.7.6-beta]: https://github.com/ErsatzTV/legacy/compare/v0.7.5-beta...v0.7.6-beta
[0.7.5-beta]: https://github.com/ErsatzTV/legacy/compare/v0.7.4-beta...v0.7.5-beta
[0.7.4-beta]: https://github.com/ErsatzTV/legacy/compare/v0.7.3-beta...v0.7.4-beta
[0.7.3-beta]: https://github.com/ErsatzTV/legacy/compare/v0.7.2-beta...v0.7.3-beta
[0.7.2-beta]: https://github.com/ErsatzTV/legacy/compare/v0.7.1-beta...v0.7.2-beta
[0.7.1-beta]: https://github.com/ErsatzTV/legacy/compare/v0.7.0-beta...v0.7.1-beta
[0.7.0-beta]: https://github.com/ErsatzTV/legacy/compare/v0.6.9-beta...v0.7.0-beta
[0.6.9-beta]: https://github.com/ErsatzTV/legacy/compare/v0.6.8-beta...v0.6.9-beta
[0.6.8-beta]: https://github.com/ErsatzTV/legacy/compare/v0.6.7-beta...v0.6.8-beta
[0.6.7-beta]: https://github.com/ErsatzTV/legacy/compare/v0.6.6-beta...v0.6.7-beta
[0.6.6-beta]: https://github.com/ErsatzTV/legacy/compare/v0.6.5-beta...v0.6.6-beta
[0.6.5-beta]: https://github.com/ErsatzTV/legacy/compare/v0.6.4-beta...v0.6.5-beta
[0.6.4-beta]: https://github.com/ErsatzTV/legacy/compare/v0.6.3-beta...v0.6.4-beta
[0.6.3-beta]: https://github.com/ErsatzTV/legacy/compare/v0.6.2-beta...v0.6.3-beta
[0.6.2-beta]: https://github.com/ErsatzTV/legacy/compare/v0.6.1-beta...v0.6.2-beta
[0.6.1-beta]: https://github.com/ErsatzTV/legacy/compare/v0.6.0-beta...v0.6.1-beta
[0.6.0-beta]: https://github.com/ErsatzTV/legacy/compare/v0.5.8-beta...v0.6.0-beta
[0.5.8-beta]: https://github.com/ErsatzTV/legacy/compare/v0.5.7-beta...v0.5.8-beta
[0.5.7-beta]: https://github.com/ErsatzTV/legacy/compare/v0.5.6-beta...v0.5.7-beta
[0.5.6-beta]: https://github.com/ErsatzTV/legacy/compare/v0.5.5-beta...v0.5.6-beta
[0.5.5-beta]: https://github.com/ErsatzTV/legacy/compare/v0.5.4-beta...v0.5.5-beta
[0.5.4-beta]: https://github.com/ErsatzTV/legacy/compare/v0.5.3-beta...v0.5.4-beta
[0.5.3-beta]: https://github.com/ErsatzTV/legacy/compare/v0.5.2-beta...v0.5.3-beta
[0.5.2-beta]: https://github.com/ErsatzTV/legacy/compare/v0.5.1-beta...v0.5.2-beta
[0.5.1-beta]: https://github.com/ErsatzTV/legacy/compare/v0.5.0-beta...v0.5.1-beta
[0.5.0-beta]: https://github.com/ErsatzTV/legacy/compare/v0.4.5-alpha...v0.5.0-beta
[0.4.5-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.4.4-alpha...v0.4.5-alpha
[0.4.4-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.4.3-alpha...v0.4.4-alpha
[0.4.3-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.4.2-alpha...v0.4.3-alpha
[0.4.2-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.4.1-alpha...v0.4.2-alpha
[0.4.1-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.4.0-alpha...v0.4.1-alpha
[0.4.0-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.3.8-alpha...v0.4.0-alpha
[0.3.8-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.3.7-alpha...v0.3.8-alpha
[0.3.7-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.3.6-alpha...v0.3.7-alpha
[0.3.6-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.3.5-alpha...v0.3.6-alpha
[0.3.5-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.3.4-alpha...v0.3.5-alpha
[0.3.4-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.3.3-alpha...v0.3.4-alpha
[0.3.3-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.3.2-alpha...v0.3.3-alpha
[0.3.2-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.3.1-alpha...v0.3.2-alpha
[0.3.1-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.3.0-alpha...v0.3.1-alpha
[0.3.0-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.2.5-alpha...v0.3.0-alpha
[0.2.5-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.2.4-alpha...v0.2.5-alpha
[0.2.4-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.2.3-alpha...v0.2.4-alpha
[0.2.3-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.2.2-alpha...v0.2.3-alpha
[0.2.2-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.2.1-alpha...v0.2.2-alpha
[0.2.1-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.2.0-alpha...v0.2.1-alpha
[0.2.0-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.1.5-alpha...v0.2.0-alpha
[0.1.5-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.1.4-alpha...v0.1.5-alpha
[0.1.4-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.1.3-alpha...v0.1.4-alpha
[0.1.3-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.1.2-alpha...v0.1.3-alpha
[0.1.2-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.1.1-alpha...v0.1.2-alpha
[0.1.1-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.1.0-alpha...v0.1.1-alpha
[0.1.0-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.62-alpha...v0.1.0-alpha
[0.0.62-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.61-alpha...v0.0.62-alpha
[0.0.61-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.60-alpha...v0.0.61-alpha
[0.0.60-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.59-alpha...v0.0.60-alpha
[0.0.59-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.58-alpha...v0.0.59-alpha
[0.0.58-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.57-alpha...v0.0.58-alpha
[0.0.57-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.56-alpha...v0.0.57-alpha
[0.0.56-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.55-alpha...v0.0.56-alpha
[0.0.55-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.54-alpha...v0.0.55-alpha
[0.0.54-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.53-alpha...v0.0.54-alpha
[0.0.53-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.52-alpha...v0.0.53-alpha
[0.0.52-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.51-alpha...v0.0.52-alpha
[0.0.51-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.50-alpha...v0.0.51-alpha
[0.0.50-alpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.49-prealpha...v0.0.50-alpha
[0.0.49-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.48-prealpha...v0.0.49-prealpha
[0.0.48-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.47-prealpha...v0.0.48-prealpha
[0.0.47-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.46-prealpha...v0.0.47-prealpha
[0.0.46-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.45-prealpha...v0.0.46-prealpha
[0.0.45-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.44-prealpha...v0.0.45-prealpha
[0.0.44-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.43-prealpha...v0.0.44-prealpha
[0.0.43-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.42-prealpha...v0.0.43-prealpha
[0.0.42-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.41-prealpha...v0.0.42-prealpha
[0.0.41-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.40-prealpha...v0.0.41-prealpha
[0.0.40-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.39-prealpha...v0.0.40-prealpha
[0.0.39-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.38-prealpha...v0.0.39-prealpha
[0.0.38-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.37-prealpha...v0.0.38-prealpha
[0.0.37-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.36-prealpha...v0.0.37-prealpha
[0.0.36-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.35-prealpha...v0.0.36-prealpha
[0.0.35-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.34-prealpha...v0.0.35-prealpha
[0.0.34-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.33-prealpha...v0.0.34-prealpha
[0.0.33-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.32-prealpha...v0.0.33-prealpha
[0.0.32-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.31-prealpha...v0.0.32-prealpha
[0.0.31-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.29-prealpha...v0.0.31-prealpha
[0.0.29-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.28-prealpha...v0.0.29-prealpha
[0.0.28-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.27-prealpha...v0.0.28-prealpha
[0.0.27-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.26-prealpha...v0.0.27-prealpha
[0.0.26-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.25-prealpha...v0.0.26-prealpha
[0.0.25-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.24-prealpha...v0.0.25-prealpha
[0.0.24-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.23-prealpha...v0.0.24-prealpha
[0.0.23-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.22-prealpha...v0.0.23-prealpha
[0.0.22-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.21-prealpha...v0.0.22-prealpha
[0.0.21-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.20-prealpha...v0.0.21-prealpha
[0.0.20-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.19-prealpha...v0.0.20-prealpha
[0.0.19-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.18-prealpha...v0.0.19-prealpha
[0.0.18-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.17-prealpha...v0.0.18-prealpha
[0.0.17-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.16-prealpha...v0.0.17-prealpha
[0.0.16-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.15-prealpha...v0.0.16-prealpha
[0.0.15-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.14-prealpha...v0.0.15-prealpha
[0.0.14-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.13-prealpha...v0.0.14-prealpha
[0.0.13-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.12-prealpha...v0.0.13-prealpha
[0.0.12-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.11-prealpha...v0.0.12-prealpha
[0.0.11-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.10-prealpha...v0.0.11-prealpha
[0.0.10-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.9-prealpha...v0.0.10-prealpha
[0.0.9-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.8-prealpha...v0.0.9-prealpha
[0.0.8-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.7-prealpha...v0.0.8-prealpha
[0.0.7-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.6-prealpha...v0.0.7-prealpha
[0.0.6-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.5-prealpha...v0.0.6-prealpha
[0.0.5-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.4-prealpha...v0.0.5-prealpha
[0.0.4-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.3-prealpha...v0.0.4-prealpha
[0.0.3-prealpha]: https://github.com/ErsatzTV/legacy/compare/v0.0.1-prealpha...v0.0.3-prealpha
[0.0.1-prealpha]: https://github.com/ErsatzTV/legacy/releases/tag/v0.0.1-prealpha
