docker exec whatsapp_972504476645 curl http://172.17.0.1:5000/api/host/health




docker ps | grep redis


docker inspect whatsapp_972504476645 | grep -A 20 "Networks"
docker logs whatsapp-whatsapp_972504476645 -f | grep -i "creds\|authenticated\|webhook\|host"

docker inspect liorgr/whatsapp-single | grep -i created


# 4. הפעל מחדש את ה-Agent
dotnet run

# 5. Provision חדש
curl -X POST http://localhost:5000/api/phones/provision \
  -H "Content-Type: application/json" \
  -d '{"phoneNumber": "+972504476645", "nickname": "lior test"}'



curl -X POST http://localhost:8133/webhooks/register \
  -H "Content-Type: application/json" \
  -d '{"url": "http://172.17.0.1:5000/api/webhook/container-event/dd84ec50-7fcc-45b8-8c79-bf4db52537cc", "secret": "manager-secret"}'






  # 1. עצור
docker stop whatsapp_972504476645

# 2. מחק auth
sudo rm -rf /opt/whatsapp-data/auth_645/*

# 3. הפעל
docker start whatsapp_972504476645

# 4. המתן וקבל QR
sleep 15
curl http://localhost:8133/qrcode


dotnet --version


curl -X POST http://localhost:5000/api/phones/provision \
  -H "Content-Type: application/json" \
  -d '{"phoneNumber": "+972504476645", "nickname": "lior test"}'

curl -X POST http://localhost:5000/api/phones/provision \
  -H "Content-Type: application/json" \
  -d '{"phoneNumber": "+97254625291", "nickname": "lior test"}'

sudo docker rm -f whatsapp_97254625291

<pre>/opt/whatsapp-data/auth_972546252291

sudo rm /opt/whatsapp-data/auth_972546252291/creds.json


ls /opt/whatsapp-data/auth_972546252291/
