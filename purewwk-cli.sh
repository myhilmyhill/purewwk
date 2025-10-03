#!/bin/bash
# PureWWK CLI Helper Script
# This script provides easy access to CLI commands for managing the PureWWK music server

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Check if we're running in a container or locally
if [ -f "/app/repos" ]; then
    # Running in container
    APP_PATH="/app/repos"
else
    # Running locally - use dotnet run
    APP_PATH="dotnet run --project $SCRIPT_DIR --"
fi

case "${1:-}" in
    rebuild-index|--rebuild-index)
        echo "Rebuilding Lucene search index..."
        $APP_PATH --rebuild-index
        ;;
    clear-cache|--clear-cache)
        echo "Clearing HLS cache..."
        $APP_PATH --clear-cache
        ;;
    help|--help|-h)
        $APP_PATH --help
        ;;
    "")
        echo "PureWWK CLI Helper"
        echo ""
        echo "Usage: $0 [command]"
        echo ""
        echo "Commands:"
        echo "  rebuild-index    Rebuild the Lucene search index"
        echo "  clear-cache      Clear the HLS cache"
        echo "  help             Show help message"
        echo ""
        echo "You can also run the application directly with: ./repos [command]"
        exit 0
        ;;
    *)
        echo "Unknown command: $1"
        echo "Run '$0 help' for usage information"
        exit 1
        ;;
esac
