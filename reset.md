# 1. מחק container
docker rm -f whatsapp_972504476645

# 2. מחק image ישן
docker rmi liorgr/whatsapp-single:latest

# 3. נקה webhooks
docker exec redis_shared redis-cli del whatsapp:webhooks





# 1. עצור ומחק את כל הקונטיינרים של WhatsApp
sudo docker stop $(docker ps -q --filter "name=whatsapp_") 2>/dev/null
sudo docker rm $(docker ps -aq --filter "name=whatsapp_") 2>/dev/null

# 2. מחק קבצי auth/data
sudo rm -rf /var/whatsapp-data/* 2>/dev/null

# עצור ומחק את כל containers של whatsapp
sudo docker rm -f $(docker ps -aq --filter "name=whatsapp")

# מחק את auth files
sudo rm -rf /opt/whatsapp-data/auth_*
sudo rm -rf /opt/whatsapp-data/logs_*
sudo rm -rf /opt/whatsapp-data/contacts_*


# או איפה שה-DataBasePath מוגדר

# 3. מחק לוגים
sudo rm -rf src/WhatsAppDockerManager/logs/*


-- 4. מחק מה-DB

 TRUNCATE TABLE phones RESTART IDENTITY CASCADE;
 TRUNCATE TABLE agent_hosts RESTART IDENTITY CASCADE;
 TRUNCATE TABLE agent_events RESTART IDENTITY CASCADE;
 TRUNCATE TABLE contacts RESTART IDENTITY CASCADE;
TRUNCATE TABLE calls RESTART IDENTITY CASCADE;
TRUNCATE TABLE ping_sender RESTART IDENTITY CASCADE;
     

docker run -d \
  --name redis_shared \
  --network whatsapp_network \
  -v $(pwd)/data/redis:/data \
  --restart unless-stopped \
  redis:alpine





select * from phones
 select * FROM contacts
 select * FROM messages
 select * FROM agent_events
 select * from agent_hosts
  select * from ping_sender