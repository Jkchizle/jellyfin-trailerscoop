# TrailerScoop (Jellyfin plugin)
Downloads official trailers from TMDb/YouTube and saves them next to your movies.

## Install (easy way)
1) Add my repo URL in Jellyfin: Dashboard ? Plugins ? Repositories ? Add
   URL: https://JKChizle.github.io/jellyfin-trailerscoop/repo/manifest.json
2) Then go to Catalog, find **TrailerScoop**, click Install.

## Manual install
Download the latest release ZIP, extract its contents into
C:\ProgramData\Jellyfin\Server\plugins\TrailerScoop_<version>\

## Config
Create/edit:
C:\ProgramData\Jellyfin\Server\plugins\configurations\2d4a0fa0-4b2b-4d26-9c6b-7b8106e8a5bf.json
{
  "TmdbApiKey": "<your TMDb key>",
  "PreferredLanguage": "en-US",
  "YtDlpPath": "C:\\Tools\\yt-dlp.exe",
  "FfmpegPath": "C:\\Tools\\ffmpeg\\bin\\ffmpeg.exe",
  "OverwriteExisting": false
}

Then restart Jellyfin and run:
Dashboard ? Scheduled Tasks ? Library ? **Fetch Trailers for Library**.
