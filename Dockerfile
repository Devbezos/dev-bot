# Railway image for the bot. It builds against dev-api and dev-client (GitHub: Devbezos/dev-api, Devbezos/dev-library),
# which Railway doesn't fetch, so the build stage clones them with GITHUB_TOKEN (a Railway variable; read-only access to
# both repos is enough). Only the published output reaches the final image, so the token doesn't.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG GITHUB_TOKEN
ARG DEV_CLIENT_REF=master
ARG DEV_API_REF=master
# Railway caches build layers, and this command never changes, so it would keep cloning old dev-client / dev-api code.
# Using the commit being built makes each new dev-bot commit clone fresh.
ARG RAILWAY_GIT_COMMIT_SHA
RUN echo "dev-bot ${RAILWAY_GIT_COMMIT_SHA}" && git clone --depth 1 --branch "$DEV_CLIENT_REF" "https://x-access-token:${GITHUB_TOKEN}@github.com/Devbezos/dev-library.git" /src/dev-client \
 && git clone --depth 1 --branch "$DEV_API_REF" "https://x-access-token:${GITHUB_TOKEN}@github.com/Devbezos/dev-api.git" /src/dev-api
COPY . /src/dev-bot
# appsettings.json holds secrets and isn't in git; the build needs the file, docker-entrypoint.sh fills it in at start.
RUN echo '{}' > /src/dev-bot/appsettings.json && dotnet publish /src/dev-bot/DevBot.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
# Voice: ffmpeg decodes sounds, and Discord.Net loads libopus.so / libsodium.so (the -dev packages provide those names).
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg libopus-dev libsodium-dev && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# The bot keeps small state files (retry queues, TCG and raid reminder state) in /app/data.
RUN mkdir -p /app/data && chown app:app /app/data
COPY docker-entrypoint.sh /docker-entrypoint.sh
# The entrypoint starts as root to write settings, then runs the bot as the image's "app" user.
ENTRYPOINT ["sh", "/docker-entrypoint.sh"]
