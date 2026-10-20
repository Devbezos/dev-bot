#!/bin/sh
# Railway keeps settings in variables: APPSETTINGS_JSON is written out as appsettings.json (the bot's and the API's
# settings in one file), and GOOGLE_CREDENTIALS_JSON as the Google service account file it names.
set -e
if [ -n "$APPSETTINGS_JSON" ]; then printf '%s' "$APPSETTINGS_JSON" > /app/appsettings.json; fi
if [ -n "$GOOGLE_CREDENTIALS_JSON" ]; then printf '%s' "$GOOGLE_CREDENTIALS_JSON" > /app/google-credentials.json; fi
exec dotnet DevBot.dll
