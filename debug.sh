docker exec whatsapp_972504476645 curl http://172.17.0.1:5000/api/host/health




docker ps | grep redis


docker inspect whatsapp_972504476645 | grep -A 20 "Networks"


docker inspect yliorgr/whatsapp-single | grep -i created


# 4. הפעל מחדש את ה-Agent
dotnet run

# 5. Provision חדש
curl -X POST http://localhost:5000/api/phones/provision \
  -H "Content-Type: application/json" \
  -d '{"phoneNumber": "+972504476645", "nickname": "lior test"}'



curl -X POST http://localhost:8133/webhooks/register \
  -H "Content-Type: application/json" \
  -d '{"url": "http://172.17.0.1:5000/api/webhook/container-event/dd84ec50-7fcc-45b8-8c79-bf4db52537cc", "secret": "manager-secret"}'