#!/bin/bash
# ════════════════════════════════════════════════════════════════════
# מחיקת כל ה-whatsapp containers + images
# ════════════════════════════════════════════════════════════════════

echo "🔍 מוצא containers של whatsapp..."

# עצור ומחק containers שמתחילים ב-whatsapp_
CONTAINERS=$(docker ps -a --filter "name=whatsapp_" --format "{{.ID}}")
if [ -n "$CONTAINERS" ]; then
    echo "🛑 עוצר containers..."
    docker stop $CONTAINERS
    echo "🗑️  מוחק containers..."
    docker rm $CONTAINERS
    echo "✅ Containers נמחקו"
else
    echo "ℹ️  אין containers של whatsapp"
fi

# מחק image
IMAGE="whatsapp-single"
if docker image inspect $IMAGE > /dev/null 2>&1; then
    echo "🗑️  מוחק image $IMAGE..."
    docker rmi $IMAGE --force
    echo "✅ Image נמחק"
else
    echo "ℹ️  Image $IMAGE לא קיים"
fi

# נקה volumes ו-networks יתומים (אופציונלי)
echo "🧹 מנקה volumes יתומים..."
docker volume prune -f

echo ""
echo "✅ ניקוי הושלם"
docker ps -a
