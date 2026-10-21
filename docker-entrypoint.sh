#!/bin/sh
# Railway keeps settings in variables: APPSETTINGS_JSON is written out as appsettings.json (the bot's and the API's
# settings in one file), and GOOGLE_CREDENTIALS_JSON as the Google service account file it names.
# The container starts as root only to write those files; the bot then runs as the unprivileged "app" user from the
# .NET image.
set -e
umask 077
if [ -n "$APPSETTINGS_JSON" ]; then printf '%s' "$APPSETTINGS_JSON" > /app/appsettings.json; fi
if [ -n "$GOOGLE_CREDENTIALS_JSON" ]; then printf '%s' "$GOOGLE_CREDENTIALS_JSON" > /app/google-credentials.json; fi
umask 022

if [ "$(id -u)" = "0" ]; then
  for f in /app/appsettings.json /app/google-credentials.json; do
    if [ -f "$f" ]; then chown app:app "$f"; fi
  done
  export HOME=/home/app
  exec setpriv --reuid=app --regid=app --init-groups dotnet DevBot.dll
fi
exec dotnet DevBot.dll
