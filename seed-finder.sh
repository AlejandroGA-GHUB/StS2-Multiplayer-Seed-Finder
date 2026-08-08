#!/usr/bin/env sh
# ---------------------------------------------------------------------------
#  Opens the seed finder in your browser. The Linux and macOS entry point.
#
#    ./seed-finder.sh             build, serve on 5173, open a browser
#    ./seed-finder.sh 8080        serve on a different port
#    ./seed-finder.sh --nobuild   skip the build (fastest restart)
#
#  The server runs in the foreground of THIS terminal, so Ctrl+C stops it and
#  nothing is left listening afterwards.
#
#  There is no app-window mode here, and there cannot be: that needs WebView2,
#  which only exists on Windows. This is the same UI, the same local server and
#  the same results, in a browser tab. seed-finder.bat is the Windows twin.
# ---------------------------------------------------------------------------
set -eu

cd "$(dirname "$0")"

# Only this project and what it depends on. Building the whole solution also builds the Windows
# app shell, which pulls down the Windows Desktop targeting packs and produces an exe that cannot
# run here.
PROJECT="src/Sts2.SeedFinder.Web"

PORT=5173
BUILD=1

for arg in "$@"; do
    case "$arg" in
        --nobuild) BUILD=0 ;;
        *) PORT="$arg" ;;
    esac
done

if ! command -v dotnet >/dev/null 2>&1; then
    echo "The .NET 10 SDK is not on your PATH: https://dotnet.microsoft.com/download" >&2
    exit 1
fi

if [ "$BUILD" = "1" ]; then
    echo "Building..."
    dotnet build -c Release --nologo -v quiet "$PROJECT"
fi

URL="http://localhost:$PORT"

# Open the browser only once the port answers, so the first paint is the app rather than a
# connection error. Backgrounded, because the server has to start for the port to open at all.
(
    # set +e for the polling: every probe before the server is up fails by design, and under
    # set -e the first of those failures would kill this subshell and the browser would never
    # open.
    set +e
    i=0
    while [ "$i" -lt 160 ]; do
        # Neither curl nor nc is assumed. Between them they cover a stock desktop install of
        # just about anything, and a plain wait is the fallback when neither is present.
        if command -v curl >/dev/null 2>&1; then
            curl -s -o /dev/null "$URL"
            if [ $? -eq 0 ]; then break; fi
        elif command -v nc >/dev/null 2>&1; then
            nc -z localhost "$PORT" >/dev/null 2>&1
            if [ $? -eq 0 ]; then break; fi
        else
            sleep 3
            break
        fi
        i=$((i + 1))
        sleep 0.25
    done

    if command -v xdg-open >/dev/null 2>&1; then xdg-open "$URL"
    elif command -v open >/dev/null 2>&1; then open "$URL"
    else echo "Open $URL in your browser."
    fi
) &

echo
echo "  Seed finder:  $URL"
echo "  Press Ctrl+C to stop it."
echo

exec dotnet run -c Release --no-build --project "$PROJECT" -- --urls "$URL"
