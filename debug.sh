docker exec whatsapp_972504476645 curl http://172.17.0.1:5000/api/host/health


curl -X POST http://localhost:8133/webhooks/register \
  -H "Content-Type: application/json" \
  -d '{"url": "http://172.17.0.1:5000/api/webhook/container-event/dd84ec50-7fcc-45b8-8c79-bf4db52537cc", "secret": "manager-secret"}'