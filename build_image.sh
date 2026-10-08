


```bash
docker rm -f redis_shared
docker volume create redis_shared_data
docker run -d --name redis_shared --network whatsapp_network \
  --restart unless-stopped --memory 512m -p 127.0.0.1:6379:6379 \
  -v redis_shared_data:/data redis:7-alpine \
  redis-server --maxmemory 384mb --maxmemory-policy volatile-lru \
               --appendonly yes --appendfsync everysec
sudo systemctl restart whatsapp-manager
```


```bash
docker exec redis_shared redis-cli config set maxmemory 384mb
docker exec redis_shared redis-cli config set maxmemory-policy volatile-lru
```