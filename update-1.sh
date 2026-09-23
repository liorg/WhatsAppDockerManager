#!/bin/bash
set -e

# --- Ensure data dirs ---
mkdir -p /opt/myapp/data/redis

APP_NAME="WhatsAppDockerManager"
REPO="liorg/WhatsAppDockerManager"
SERVICE_NAME="whatsapp-manager"
BASE_DIR="/opt/myapp"
RELEASES_DIR="$BASE_DIR/releases"
CURRENT_LINK="$BASE_DIR/current"
VERSION_FILE="$BASE_DIR/current_version"

mkdir -p "$RELEASES_DIR"

LATEST_TAG=$(curl -s "https://api.github.com/repos/$REPO/releases/latest" | jq -r .tag_name)
if [ "$LATEST_TAG" = "null" ] || [ -z "$LATEST_TAG" ]; then
  echo "No latest release found"
  exit 1
fi

CURRENT_VERSION=""
if [ -f "$VERSION_FILE" ]; then
  CURRENT_VERSION=$(cat "$VERSION_FILE")
fi

echo "Current: $CURRENT_VERSION"
echo "Latest:  $LATEST_TAG"

# --- prepull: ה-Manager הרץ מושך את כל ה-images של providers (טבלת providers) ---
prepull_images() {
  echo "Pre-pulling provider images via Manager..."
  curl -fsS -m 900 -X POST "http://localhost:5000/api/images/prepull" >/dev/null \
    && echo "Images up to date" \
    || echo "WARN: prepull failed — continuing with cached images"
}

if [ "$CURRENT_VERSION" = "$LATEST_TAG" ]; then
  echo "Already up to date"
  # גם כשאין release חדש — מוודאים שה-images ב-cache עדכניים
  prepull_images
  exit 0
fi

ZIP_URL=$(curl -s "https://api.github.com/repos/$REPO/releases/latest" \
  | jq -r '.assets[] | select(.name | endswith(".zip")) | .browser_download_url' \
  | head -n 1)

if [ -z "$ZIP_URL" ]; then
  echo "No ZIP asset found"
  exit 1
fi

TARGET_DIR="$RELEASES_DIR/$LATEST_TAG"
TMP_ZIP=$(mktemp "/tmp/${APP_NAME}-${LATEST_TAG}.XXXXXX.zip")

echo "Downloading $ZIP_URL"
curl -fL -o "$TMP_ZIP" "$ZIP_URL"

rm -rf "$TARGET_DIR"
mkdir -p "$TARGET_DIR"
unzip -q "$TMP_ZIP" -d "$TARGET_DIR"
rm -f "$TMP_ZIP"

# --- fix publish folder ---
if [ -d "$TARGET_DIR/publish" ]; then
  mv "$TARGET_DIR/publish"/* "$TARGET_DIR/"
  rm -rf "$TARGET_DIR/publish"
fi

# --- prepull BEFORE switching: ה-Manager הישן עדיין רץ → restart ישתמש ב-cache עדכני ---
prepull_images

# --- fix symlink ---
rm -rf "$CURRENT_LINK"
ln -sfn "$TARGET_DIR" "$CURRENT_LINK"

echo "$LATEST_TAG" > "$VERSION_FILE"

echo "Restarting service..."
sudo systemctl restart "$SERVICE_NAME"

echo "Updated to $LATEST_TAG"