# 1. עצור
docker stop whatsapp_972504476645

# 2. מחק auth
sudo rm -rf /opt/whatsapp-data/auth_645/*

# 3. הפעל
docker start whatsapp_972504476645

# 4. המתן וקבל QR
sleep 15
curl http://localhost:8133/qrcode